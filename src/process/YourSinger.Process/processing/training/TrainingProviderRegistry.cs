using System.Security.Cryptography;
using System.Text;
using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// Enabled は利用者の明示的な有効化の状態。登録しただけでは false で、外部コードは起動しない。
public sealed class TrainingProviderEntry(TrainingProviderDescriptor provider, string root, string origin, string hash)
{
    public TrainingProviderDescriptor Provider { get; } = provider;
    public string Root { get; } = root;
    // 利用者に出所として示す文字列(Providerのフォルダなど)。有効化時に確認する。
    public string Origin { get; } = origin;
    // provider.json・実行ファイル・資源のhashから決まる。内容が変われば別の版として扱う。
    public string Hash { get; } = hash;
    public bool Enabled { get; internal set; }
    public string EntryPath => TrainingProviderValidator.Contain(Root, Provider.Entry);
}

public sealed record TrainingProviderRegistration(bool Accepted, TrainingProviderEntry? Entry, string? Failure);

// {
//   責務: [TrainingProviderRegistry: 第三者学習Worker Providerを検証して登録し、明示的な有効化と版の固定を管理する]
//   補足: [
//     検証に失敗したProviderはそれだけを拒否して記録し、他のProviderと既存Trainerには影響しない。
//     外部コードを含むため、登録しただけでは起動しない。OS の sandbox は提供せず、保証もしない。
//     旧ジョブは (provider_id, version, hash) で参照し、内容が変わった・削除されたProviderでは再現できないとして失敗する。
//   ]
// }
public sealed class TrainingProviderRegistry
{
    private readonly List<TrainingProviderEntry> _entries = [];
    private readonly List<(string Origin, string Reason)> _failures = [];

    public IReadOnlyList<(string Origin, string Reason)> Failures => _failures;

    public IReadOnlyList<TrainingProviderEntry> Entries => _entries
        .OrderBy(x => x.Provider.ProviderId, StringComparer.Ordinal)
        .ThenBy(x => x.Provider.Version, Comparer<string>.Create(TexturePresetValidator.CompareVersions)).ToArray();

    // {
    //   責務: [Register: Providerのフォルダを検証して登録する(無効状態)]
    //   処理: [
    //     1: provider.json を読み、形式・版・権限・パスを検証する
    //     2: entry・ライセンス・資源の実在とhashを確認する
    //     3: 組み込みIDの乗っ取りと、同じ (id, version) の別内容を拒否する
    //   ]
    // }
    public TrainingProviderRegistration Register(string providerRoot)
    {
        var origin = Path.GetFullPath(providerRoot);
        try
        {
            var manifest = Path.Combine(origin, "provider.json");
            if (!File.Exists(manifest)) return Reject(origin, "provider.json がありません。");
            var provider = TrainingProviderValidator.Parse(File.ReadAllText(manifest));
            if (provider.ProviderId.StartsWith("builtin.", StringComparison.Ordinal))
                return Reject(origin, $"組み込みのIDは使えません: {provider.ProviderId}");
            var entryPath = TrainingProviderValidator.Contain(origin, provider.Entry);
            if (!File.Exists(entryPath)) return Reject(origin, $"実行ファイルがありません: {provider.Entry}");
            if (!File.Exists(TrainingProviderValidator.Contain(origin, provider.LicenseFile)))
                return Reject(origin, $"ライセンスファイルがありません: {provider.LicenseFile}");
            var resources = TrainingProviderValidator.VerifyResources(origin, provider);
            var hash = ComputeHash(File.ReadAllText(manifest), entryPath, resources);
            var same = _entries.FirstOrDefault(x => x.Provider.ProviderId == provider.ProviderId && x.Provider.Version == provider.Version);
            if (same is not null)
                return same.Hash == hash
                    ? new TrainingProviderRegistration(true, same, null)
                    : Reject(origin, $"同じID・versionで内容が異なります: {provider.ProviderId} {provider.Version}");
            var entry = new TrainingProviderEntry(provider, origin, origin, hash);
            _entries.Add(entry);
            return new TrainingProviderRegistration(true, entry, null);
        }
        catch (InvalidDataException exception)
        {
            return Reject(origin, exception.Message);
        }
    }

    // {
    //   責務: [Enable: 利用者が出所を確認したProviderを有効にする]
    //   引数: [acknowledgedOrigin: 利用者に提示した出所。登録時の出所と一致しなければ有効にしない]
    //   エラー: [未登録・出所の不一致で InvalidOperationException]
    // }
    public void Enable(string providerId, string version, string acknowledgedOrigin)
    {
        var entry = Find(providerId, version) ?? throw new InvalidOperationException($"登録されていないProviderです: {providerId} {version}");
        if (!string.Equals(Path.GetFullPath(acknowledgedOrigin), entry.Origin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("提示した出所が登録時の出所と一致しません。出所を確認してから有効にしてください。");
        entry.Enabled = true;
    }

    public void Disable(string providerId, string version)
    {
        var entry = Find(providerId, version) ?? throw new InvalidOperationException($"登録されていないProviderです: {providerId} {version}");
        entry.Enabled = false;
    }

    public TrainingProviderEntry? Find(string providerId, string version) =>
        _entries.FirstOrDefault(x => x.Provider.ProviderId == providerId && x.Provider.Version == version);

    // {
    //   責務: [Resolve: ジョブが保存した参照(id・version・hash)と同じ内容のProviderを返す]
    //   エラー: [削除された・内容が変わった場合に InvalidOperationException(別の内容へ黙って再解決しない)]
    // }
    public TrainingProviderEntry Resolve(string providerId, string version, string hash)
    {
        var entry = Find(providerId, version)
            ?? throw new InvalidOperationException($"このジョブのProvider {providerId} {version} は登録されていないため再現できません。");
        return entry.Hash == hash
            ? entry
            : throw new InvalidOperationException($"Provider {providerId} {version} の内容が、ジョブの作成時と変わっているため再現できません。");
    }

    // 実行直前に、登録後の改変(実行ファイル・資源の差し替え)を検出する。
    public static void EnsureUnchanged(TrainingProviderEntry entry)
    {
        var manifest = Path.Combine(entry.Root, "provider.json");
        var resources = TrainingProviderValidator.VerifyResources(entry.Root, entry.Provider);
        var hash = ComputeHash(File.ReadAllText(manifest), entry.EntryPath, resources);
        if (hash != entry.Hash)
            throw new InvalidOperationException($"Provider {entry.Provider.ProviderId} の内容が登録後に変更されました。登録し直してください。");
    }

    private static string ComputeHash(string manifestText, string entryPath, IReadOnlyList<TrainingProviderResourceFile> resources)
    {
        var canonical = string.Join('\n', manifestText,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entryPath))),
            string.Join(';', resources.OrderBy(x => x.Path, StringComparer.Ordinal).Select(x => $"{x.Path}:{x.Sha256}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private TrainingProviderRegistration Reject(string origin, string reason)
    {
        _failures.Add((origin, reason));
        return new TrainingProviderRegistration(false, null, reason);
    }
}
