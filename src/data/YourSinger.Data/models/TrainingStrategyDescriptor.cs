namespace YourSinger.Data.Models;

// 学習戦略の宣言(strategy.json の内容)。機械検証用 schema:
// docs/design/v1-training/schema/training-strategy.schema.json。宣言は実能力を意味しない
// (未実装の operation を含む戦略は登録時に拒否または unsupported とする)。
public sealed class TrainingStrategyDescriptor
{
    public const string CurrentSchemaVersion = "1";

    public required string SchemaVersion { get; init; }
    public required string StrategyId { get; init; }
    public required string Version { get; init; }
    public required string DisplayNameJa { get; init; }
    public required string DescriptionJa { get; init; }
    public required List<string> SupportedTargets { get; init; }
    // 空なら汎用(特定のプリセットに限らない)。
    public required List<string> SupportedPresetIds { get; init; }
    public required List<StrategyRequirement> Requirements { get; init; }
    public required List<string> RequiredCapabilities { get; init; }
    public required List<StrategyStage> Stages { get; init; }
    public required List<string> ExpectedOutputTypes { get; init; }
    public string? ResourceManifest { get; init; }
}

// 成立条件1件。算術式や任意コードは持たない(role × metric × op × value のみ)。
public sealed class StrategyRequirement
{
    public required string Role { get; init; }
    public required string Metric { get; init; }
    public required string Op { get; init; }
    public required double Value { get; init; }
}

public sealed class StrategyStage
{
    public required string Id { get; init; }
    public required string OperationId { get; init; }
    public required List<string> DependsOn { get; init; }
    public required List<string> InputRoles { get; init; }
    public required List<string> OutputTypes { get; init; }
}

// ジョブが、計画時に選んだ戦略とプリセットを再現できるよう保存する参照。
// 戦略の登録・削除があっても、保存済みジョブの内容は変わらない。
public sealed class TrainingStrategyReference
{
    public required string StrategyId { get; init; }
    public required string Version { get; init; }
    public required string Hash { get; init; }
    public string? ProviderId { get; init; }
    // 第三者Providerを使う戦略では、その版と内容のhash(旧ジョブを別の内容へ黙って再解決しないための固定)。
    public string? ProviderVersion { get; init; }
    public string? ProviderHash { get; init; }
    public string? PresetId { get; init; }
    public string? PresetVersion { get; init; }
    // 計画に使った棚卸し(SpeakerDataInventory.InventoryRevision)。
    public string? InventoryRevision { get; init; }
}

public static class TrainingStrategyVocabulary
{
    public static readonly IReadOnlyList<string> Roles =
        ["speech_teacher", "singing_teacher", "identity_reference", "texture_reference", "feature_only"];

    public static readonly IReadOnlyList<string> Metrics =
    [
        "eligible_seconds", "independent_utterances", "independent_phrases",
        "distinct_sources", "phoneme_coverage_count", "pitch_bins_observed"
    ];

    public static readonly IReadOnlyList<string> Ops = ["gte", "lte", "eq", "gt", "lt"];

    public static readonly IReadOnlyList<string> StageOutputTypes =
    [
        "tts_base", "singing_base", "speaker_identity", "speaker_features", "talk_model",
        "singing_model", "model_artifact", "verification_report", "audio_preview"
    ];

    public static readonly IReadOnlyList<string> ExpectedOutputTypes = ["talk_model", "singing_model"];
}
