using System.Security.Cryptography;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Ml.Bridge;

public sealed record PluginArtifact(string Path, string Sha256);

public sealed record PluginExecutionResult(string ProviderId, string OperationId, IReadOnlyList<PluginArtifact> Artifacts);

// {
//   責務: [PluginWorkerAdapter: 有効化された第三者Providerの宣言済みoperationだけを、既存のMlWorkerClient経由で実行する]
//   フィールド: [_registry: 登録簿, _timeout: 1回の実行の上限, _clientFactory: 実行ファイルからクライアントを作る関数]
//   処理: [1: 版・有効化・operation宣言・内容の不変を確認 2: 1起動1要求で実行 3: 応答の版・出力ファイルのhash・未宣言ファイルを検証]
//   補足: [
//     OS の sandbox は提供しない。要求権限は宣言との照合のみで、プロセスの権限を制限するものではない。
//     取消・子プロセス回収は MlWorkerClient の共通実装(プロセスツリーのKill)を使う。
//     MlWorkerClient の request 形式(request_id/command/payload)を再利用し、command に operation_id を入れる。
//   ]
// }
public sealed class PluginWorkerAdapter(
    TrainingProviderRegistry registry, TimeSpan timeout, Func<string, MlWorkerClient>? clientFactory = null)
{
    private readonly Func<string, MlWorkerClient> _clientFactory = clientFactory ?? (path => new MlWorkerClient(path));

    // {
    //   責務: [ExecuteAsync: Providerのoperationを実行し、出力ファイルを検証して返す]
    //   引数: [providerRef: ジョブが保存した参照, operationId: 宣言済みのoperation, jobOutputDirectory: ジョブの出力先, input: operationへの入力]
    //   戻り値: [result: 検証済みの出力ファイル一覧]
    //   エラー: [未登録・内容の変化・未有効化・未宣言operation・タイムアウト・応答の不正・hash不一致・未宣言ファイルで例外(成功扱いにしない)]
    // }
    public async Task<PluginExecutionResult> ExecuteAsync(
        TrainingStrategyReference providerRef, string operationId, string jobOutputDirectory, object input,
        CancellationToken cancellationToken = default)
    {
        var providerId = providerRef.ProviderId
            ?? throw new InvalidOperationException("この戦略参照にはProviderがありません。");
        var entry = registry.Resolve(providerId, providerRef.ProviderVersion
            ?? throw new InvalidOperationException("Providerのversionがありません。"), providerRef.ProviderHash
            ?? throw new InvalidOperationException("Providerのhashがありません。"));
        if (!entry.Enabled)
            throw new InvalidOperationException($"Provider {providerId} は有効化されていません。出所を確認して有効にしてください。");
        var operation = entry.Provider.Operations.FirstOrDefault(x => x.OperationId == operationId)
            ?? throw new InvalidOperationException($"Provider {providerId} は operation {operationId} を宣言していません。");
        TrainingProviderRegistry.EnsureUnchanged(entry);

        Directory.CreateDirectory(jobOutputDirectory);
        var before = ListFiles(jobOutputDirectory);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        JsonElement response;
        try
        {
            response = await _clientFactory(entry.EntryPath).SendAsync(operationId, new
            {
                protocol_version = TrainingProviderDescriptor.CurrentProtocolVersion, provider_id = providerId,
                operation_id = operationId, operation_version = operation.Version,
                permissions = entry.Provider.PermissionsRequested, job_output_dir = Path.GetFullPath(jobOutputDirectory), input
            }, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Provider {providerId} の operation {operationId} が {timeout.TotalSeconds:0}秒以内に終わりませんでした。");
        }
        return Verify(entry, operationId, jobOutputDirectory, before, response);
    }

    private static PluginExecutionResult Verify(
        TrainingProviderEntry entry, string operationId, string jobOutputDirectory, HashSet<string> before, JsonElement response)
    {
        var providerId = entry.Provider.ProviderId;
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("protocol_version", out var protocol) || protocol.GetString() != TrainingProviderDescriptor.CurrentProtocolVersion)
            throw new InvalidDataException($"Provider {providerId} の応答のprotocol_versionが一致しません。");
        if (!response.TryGetProperty("operation_id", out var op) || op.GetString() != operationId)
            throw new InvalidDataException($"Provider {providerId} の応答のoperation_idが一致しません。");
        if (!response.TryGetProperty("artifacts", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Provider {providerId} の応答にartifacts配列がありません。");

        var artifacts = new List<PluginArtifact>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Provider {providerId} のartifactの形式が不正です(path・sha256が必要)。");
            artifacts.Add(new PluginArtifact(path.GetString()!, sha.GetString()!.ToLowerInvariant()));
        }
        if (artifacts.Count > 0 && !entry.Provider.PermissionsRequested.Contains("write_job_artifacts"))
            throw new InvalidDataException($"Provider {providerId} は write_job_artifacts を要求していないのに出力を返しました。");

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            var full = TrainingProviderValidator.Contain(jobOutputDirectory, artifact.Path);
            if (!File.Exists(full)) throw new InvalidDataException($"宣言された出力ファイルがありません: {artifact.Path}");
            if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))) != artifact.Sha256)
                throw new InvalidDataException($"出力ファイルのhashが一致しません: {artifact.Path}");
            declared.Add(Path.GetFullPath(full));
        }
        var unexpected = ListFiles(jobOutputDirectory).Except(before, StringComparer.OrdinalIgnoreCase)
            .Except(declared, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (unexpected.Length > 0)
            throw new InvalidDataException($"宣言されていない出力ファイルがあります: {string.Join(", ", unexpected.Select(Path.GetFileName))}");
        return new PluginExecutionResult(providerId, operationId, artifacts);
    }

    private static HashSet<string> ListFiles(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
