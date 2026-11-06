using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

public sealed class TrainingInputFingerprintInput
{
    // 観測データsnapshotのhash(元音声・手動修正・分類修正・補正ON/OFF・補完候補を含む。TrainingJobRecord.DatasetFingerprint)。
    public required string DatasetFingerprint { get; init; }
    // 質感・モード・シード・初期重みの契約hash(TrainingJobContractFingerprint)。
    public required string ContractFingerprint { get; init; }
    public required string Backend { get; init; }
    public required string BackendRevision { get; init; }
    // 出力差に影響するtrainer設定のsha256。無ければ null。
    public string? TrainerConfigSha256 { get; init; }
    public required TrainingAssessmentRecord Assessment { get; init; }
}

// 学習入力のfingerprint(input_fingerprint)。D-07: 解決済みモードの実行結果を含む run_fingerprint は #88 で確定する。
// 正本: docs/design/v1-training/contracts-and-weighting.md「5. 再計算・指紋・キャッシュ」。
public static class TrainingInputFingerprint
{
    public const string Version = "training-input-v1";

    // {
    //   責務: [Compute: 素材・評価・重み・学習方式・backendから決定的なhashを返す]
    //   処理: [
    //     1: 入力の必須項目を検証する
    //     2: 区間をSegmentId昇順で正規化し、数値は不変カルチャの往復可能な形式にする
    //     3: SHA-256を小文字16進で返す
    //   ]
    //   エラー: [必須項目が空、または区間が重複・非昇順のとき ArgumentException]
    // }
    public static string Compute(TrainingInputFingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Require(input.DatasetFingerprint, "素材のfingerprint");
        Require(input.ContractFingerprint, "学習方式のfingerprint");
        Require(input.Backend, "backend");
        Require(input.BackendRevision, "backend revision");
        var a = input.Assessment ?? throw new ArgumentException("評価がありません。", nameof(input));
        var lines = new List<string>
        {
            Version, $"dataset={input.DatasetFingerprint}", $"contract={input.ContractFingerprint}",
            $"backend={input.Backend}@{input.BackendRevision}", $"trainer_config={input.TrainerConfigSha256 ?? "none"}",
            $"policy={a.PolicyVersion}", $"speaker={a.SpeakerId}", $"target={a.Target}",
            $"source={a.SourceDatasetFingerprint}", $"override={a.ManualOverrideRevision ?? "none"}"
        };
        var previous = (string?)null;
        foreach (var item in a.Items)
        {
            if (previous is not null && string.CompareOrdinal(previous, item.SegmentId) >= 0)
                throw new ArgumentException($"評価の区間が昇順ではない、または重複しています: {item.SegmentId}", nameof(input));
            previous = item.SegmentId;
            lines.Add(Describe(item));
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private static string Describe(TrainingAssessmentItem item)
    {
        var components = string.Join(',', item.Quality.Components.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key}:{x.Value.Status}:{Number(x.Value.Value)}"));
        return string.Join('|', item.SegmentId, item.Role ?? "none", item.Eligibility,
            string.Join(',', item.ReasonCodes.Order(StringComparer.Ordinal)),
            $"q={item.Quality.Status}:{Number(item.Quality.Value)}[{components}]",
            $"u={item.Utility.Status}:{Number(item.Utility.Value)}", $"raw={Number(item.RawWeight)}",
            $"w={Number(item.TrainingWeight)}");
    }

    private static string Number(double? value) =>
        value is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : "null";

    private static void Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{label}が空です。");
    }
}
