using YourSinger.Data.Models;

namespace YourSinger.Data.Processing;

// 旧UniversalVoiceDatasetの信頼度をAssessmentの品質成分へ取り込む。原UVDは読むだけで変更しない。
public static class TrainingAssessmentLegacyImporter
{
    public const string AsrComponent = "asr";
    public const string AlignmentComponent = "alignment";

    // {
    //   責務: [ImportComponents: UVDの区間ごとにASR・alignmentの品質成分を作る]
    //   処理: [1: 区間ごとにFromLegacyConfidenceで変換する 2: SegmentId昇順で返す]
    //   戻り値: [components: segment_id -> 成分名 -> 測定値。UVDは変更しない]
    // }
    public static IReadOnlyDictionary<string, Dictionary<string, TrainingMeasurement>> ImportComponents(
        UniversalVoiceDatasetRecord dataset) =>
        dataset.Segments.OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToDictionary(
            x => x.SegmentId,
            x => new Dictionary<string, TrainingMeasurement>(StringComparer.Ordinal)
            {
                [AsrComponent] = FromLegacyConfidence(x.AsrConfidence, x.AsrConfidenceStatus),
                [AlignmentComponent] = FromLegacyConfidence(x.AlignmentConfidence, x.AlignmentConfidenceStatus)
            }, StringComparer.Ordinal);

    // {
    //   責務: [FromLegacyConfidence: 値だけから測定済み・未校正を推測せず、記録された状態だけで写す]
    //   処理: [
    //     1: 状態が measured / uncalibrated / unavailable / invalid と明示されていればそれを使う
    //     2: それ以外(legacy_unknown・未知の語)は legacy_unknown とし、有限な値だけ保持する
    //     3: 範囲外・非有限の値は measured のとき invalid、他は値を捨てる
    //   ]
    //   戻り値: [measurement: 値と状態]
    // }
    public static TrainingMeasurement FromLegacyConfidence(double value, string? status)
    {
        var usable = double.IsFinite(value) && value is >= 0 and <= 1;
        return status switch
        {
            ConfidenceStatus.Measured => usable
                ? new() { Value = value, Status = ConfidenceStatus.Measured }
                : new() { Value = null, Status = ConfidenceStatus.Invalid },
            ConfidenceStatus.Uncalibrated => new() { Value = usable ? value : null, Status = ConfidenceStatus.Uncalibrated },
            ConfidenceStatus.Unavailable => new() { Value = null, Status = ConfidenceStatus.Unavailable },
            ConfidenceStatus.Invalid => new() { Value = null, Status = ConfidenceStatus.Invalid },
            _ => new() { Value = usable ? value : null, Status = ConfidenceStatus.LegacyUnknown }
        };
    }
}
