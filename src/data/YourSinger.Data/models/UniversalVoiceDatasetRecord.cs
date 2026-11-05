namespace YourSinger.Data.Models;

public sealed class UniversalVoiceDatasetRecord
{
    public required string SchemaVersion { get; init; }
    public required string StageVersion { get; init; }
    public List<UniversalVoiceSegment> Segments { get; init; } = [];
    public CoverageRecord Coverage { get; init; } = new();
    public List<FeatureProvenanceRecord> Provenance { get; init; } = [];
    public List<UserFeatureOverrideRecord> Overrides { get; init; } = [];
}

public sealed class UniversalVoiceSegment
{
    public const string RequiredSingingTrainingAlignmentMethod = "mfa-japanese-phone-forced-alignment-v1";

    // {
    //   責務: [HasValidSingingTrainingAlignment: 歌唱学習へ渡せる検証済み音素alignmentか判定する]
    //   処理: [
    //     1: 必須のforced alignment方式と信頼度を確認する
    //     2: 音素、時刻、信頼度の値を検証する
    //     3: 音素時刻の順序と重なりを確認する
    //   ]
    //   戻り値: [isValid: 学習用alignmentの条件をすべて満たす場合はtrue]
    // }
    public bool HasValidSingingTrainingAlignment() =>
        AlignmentMethod == RequiredSingingTrainingAlignmentMethod &&
        double.IsFinite(AlignmentConfidence) && AlignmentConfidence is >= 0 and <= 1 &&
        Phonemes is { Count: > 0 } &&
        Phonemes.All(x =>
            x is not null &&
            !string.IsNullOrWhiteSpace(x.Phoneme) &&
            double.IsFinite(x.StartSec) && double.IsFinite(x.EndSec) &&
            double.IsFinite(x.Confidence) && x.Confidence is >= 0 and <= 1 &&
            x.StartSec >= 0 && x.EndSec > x.StartSec) &&
        Phonemes.Zip(Phonemes.Skip(1), (previous, current) =>
            current.StartSec >= previous.EndSec - 1e-6).All(x => x);

    // {
    //   責務: [IsAsrConfidenceMeasured: ASR信頼度が実測値で、低信頼の判定に使えるか返す]
    //   処理: [1: 状態が実測なら使う 2: 未校正・算出不可なら使わない 3: 旧形式は0を判別不可として使わない]
    //   戻り値: [isMeasured: 判定に使える実測値ならtrue]
    // }
    public bool IsAsrConfidenceMeasured() => AsrConfidenceStatus switch
    {
        ConfidenceStatus.Measured => true,
        ConfidenceStatus.Uncalibrated or ConfidenceStatus.Unavailable or ConfidenceStatus.Invalid => false,
        _ => AsrConfidence > 0
    };

    // {
    //   責務: [IsAlignmentConfidenceMeasured: 音素境界の信頼度が実測値で、低信頼の判定に使えるか返す]
    //   処理: [1: 状態が実測なら使う 2: 未校正・算出不可なら使わない 3: 旧形式はMFA実測方式の0だけを未校正として使わない]
    //   戻り値: [isMeasured: 判定に使える実測値ならtrue]
    // }
    public bool IsAlignmentConfidenceMeasured() => AlignmentConfidenceStatus switch
    {
        ConfidenceStatus.Measured => true,
        ConfidenceStatus.Uncalibrated or ConfidenceStatus.Unavailable or ConfidenceStatus.Invalid => false,
        _ => !(AlignmentMethod == RequiredSingingTrainingAlignmentMethod && AlignmentConfidence == 0)
    };

    public required string SegmentId { get; init; }
    public required string SourceId { get; init; }
    public string? SpeakerId { get; set; }
    public SegmentContentType ContentType { get; set; }
    public required string AudioPath { get; init; }
    public required string CacheKey { get; init; }

    public string Transcript { get; set; } = string.Empty;
    public double AsrConfidence { get; set; }
    // 信頼度の意味。measured=実測、uncalibrated=未校正、unavailable=算出不可、legacy_unknown=旧形式で判別不可
    public string AsrConfidenceStatus { get; set; } = ConfidenceStatus.LegacyUnknown;

    public List<PhonemeTimingRecord> Phonemes { get; init; } = [];
    public double AlignmentConfidence { get; set; }
    public string AlignmentConfidenceStatus { get; set; } = ConfidenceStatus.LegacyUnknown;
    public string AlignmentMethod { get; set; } = "legacy-uniform-placeholder";
    public string? AlignmentWarning { get; set; }

    public string F0FeaturePath { get; set; } = string.Empty;
    public string EnergyFeaturePath { get; set; } = string.Empty;
    public string VoicingFeaturePath { get; set; } = string.Empty;

    public double PitchReliability { get; set; }
    public double MeanF0Hz { get; set; }
    public double F0StdDevHz { get; set; }
    public double MeanEnergy { get; set; }
    public double VoicedRatio { get; set; }

    public StyleProsodyRecord StyleProsody { get; set; } = new();
    public List<double> SpeakerEmbedding { get; init; } = [];
}

public static class ConfidenceStatus
{
    public const string Measured = "measured";
    public const string Uncalibrated = "uncalibrated";
    public const string Unavailable = "unavailable";
    public const string LegacyUnknown = "legacy_unknown";
    public const string Invalid = "invalid";
}

public sealed class PhonemeTimingRecord
{
    public required string Phoneme { get; init; }
    public double StartSec { get; init; }
    public double EndSec { get; init; }
    public double Confidence { get; init; }
}

public sealed class StyleProsodyRecord
{
    public double SpeakingRate { get; set; }
    public double PitchRangeSemitones { get; set; }
    public double EnergyVariation { get; set; }
    public double PauseRatio { get; set; }
}

public sealed class CoverageRecord
{
    public Dictionary<string, int> PhonemeCounts { get; init; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, int> ContentTypeCounts { get; init; } =
        new(StringComparer.Ordinal);
    public SingingCoverageSummary SingingQuality { get; init; } = new();
}

public sealed class FeatureProvenanceRecord
{
    public required string SegmentId { get; init; }
    public required string FeatureName { get; init; }
    public required string StageVersion { get; init; }
    public required string Method { get; init; }
    public required string CacheKey { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class UserFeatureOverrideRecord
{
    public required string SegmentId { get; init; }
    public required string Field { get; init; }
    public required string Value { get; init; }
    public DateTimeOffset ChangedAt { get; init; } = DateTimeOffset.UtcNow;
}
