namespace YourSinger.Data.Models;

// 学習用の区間評価(適格性・品質・価値・重み)。原本のUniversalVoiceDatasetRecordは改変せず別recordとして保存する。
// 正本: docs/design/v1-training/contracts-and-weighting.md「1. 入出力データ契約」。
public sealed class TrainingAssessmentRecord
{
    public const string CurrentSchemaVersion = "1";

    public required string SchemaVersion { get; init; }
    public required string PolicyVersion { get; init; }
    public required string JobId { get; init; }
    public required string SpeakerId { get; init; }
    // 評価の対象用途。talk または singing(TrainingAssessmentVocabulary.Targets)。
    public required string Target { get; init; }
    public required string SourceDatasetFingerprint { get; init; }
    // 手動修正の版。修正が無い場合は省略(null)。
    public string? ManualOverrideRevision { get; init; }
    // SegmentId昇順。
    public required List<TrainingAssessmentItem> Items { get; init; }
}

public sealed class TrainingAssessmentItem
{
    public required string SegmentId { get; init; }
    // 区間の使われ方(D-04)。省略可。値は TrainingAssessmentVocabulary.Roles。
    public string? Role { get; init; }
    public required string Eligibility { get; init; }
    public required List<string> ReasonCodes { get; init; }
    public required TrainingQualityAssessment Quality { get; init; }
    public required TrainingUtilityAssessment Utility { get; init; }
    public required double RawWeight { get; init; }
    public required double TrainingWeight { get; init; }
}

// 値と状態の組。Value が null(未計測)と 0.0(測定済みの0)はJSON上でも区別する。
public sealed class TrainingMeasurement
{
    public required double? Value { get; init; }
    public required string Status { get; init; }
}

public sealed class TrainingQualityAssessment
{
    public required double? Value { get; init; }
    public required string Status { get; init; }
    public required Dictionary<string, TrainingMeasurement> Components { get; init; }
}

public sealed class TrainingUtilityAssessment
{
    public required double? Value { get; init; }
    public required string Status { get; init; }
    public required List<string> ReasonCodes { get; init; }
}

// 閉じた語彙。状態語彙はD-04/D-05/D-20、reason_codesは設計の例に限る。
public static class TrainingAssessmentVocabulary
{
    public static readonly IReadOnlyList<string> Targets = ["talk", "singing"];
    public static readonly IReadOnlyList<string> Eligibilities = ["eligible", "needs_review", "excluded"];
    public static readonly IReadOnlyList<string> Roles = ["primary", "feature_only", "review", "excluded"];
    public static readonly IReadOnlyList<string> Statuses =
    [
        ConfidenceStatus.Measured, ConfidenceStatus.Uncalibrated, ConfidenceStatus.Unavailable,
        ConfidenceStatus.LegacyUnknown, ConfidenceStatus.Invalid
    ];
    public static readonly IReadOnlyList<string> ReasonCodes =
    [
        "user_excluded", "wrong_speaker", "unreadable_audio", "no_usable_voice",
        "wrong_content_type", "alignment_required", "rare_phoneme", "speaker_unconfirmed"
    ];
}
