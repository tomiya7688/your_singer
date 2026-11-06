using System.Security.Cryptography;
using System.Text;
using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// ジョブの学習方式(質感・モード・シード・初期化)だけから決まるハッシュ。素材の指紋(DatasetFingerprint)とは独立で、
// 質感やモードを変えてもデータの解析結果(前段)は変わらない。入力全体のfingerprintは #85 で組み立てる。
public static class TrainingJobContractFingerprint
{
    // {
    //   責務: [Compute: 学習方式の契約から決定的なハッシュを返す]
    //   処理: [1: 順序の影響を受ける要素を並べ替えて正規化し、SHA-256を16進で返す]
    //   戻り値: [fingerprint: 小文字16進のSHA-256。旧ジョブ(学習方式なし)は例外]
    // }
    public static string Compute(TrainingJobRecord job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.IsLegacyWithoutTrainingContract || job.RequestedTexture is null)
            throw new InvalidOperationException("学習方式の契約を持たない旧ジョブにはfingerprintを作成できません。再作成してください。");
        var texture = job.RequestedTexture;
        var parts = new List<string>
        {
            "training-job-contract-v1",
            $"target={job.Target}",
            $"preset={texture.Preset}{(texture.PresetVersion is null ? string.Empty : "@" + texture.PresetVersion)}",
            $"description={texture.Description ?? string.Empty}",
            $"references={string.Join(',', texture.ReferenceSegmentIds.Order(StringComparer.Ordinal))}",
            $"requested={job.RequestedTrainingMode}",
            $"resolved={job.ResolvedTrainingMode?.ToString() ?? "unresolved"}",
            $"seed={job.Seed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}",
            $"init={job.Initialization?.Type ?? "none"}"
        };
        foreach (var component in (job.Initialization?.Components ?? []).OrderBy(x => x.Name, StringComparer.Ordinal))
            parts.Add($"component={component.Name}|{component.Sha256.ToLowerInvariant()}|{component.Path}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
    }
}
