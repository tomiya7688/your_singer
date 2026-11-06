namespace YourSinger.Data.Models;

// 質感プリセットの宣言(preset.json の内容)。機械検証用 schema:
// docs/design/v1-training/schema/texture-preset.schema.json。
// プリセットは「表現目標の束」で、出力 target(talk / singing)とは独立に選べる。
public sealed class TexturePresetRecord
{
    public const string CurrentSchemaVersion = "1";

    public required string SchemaVersion { get; init; }
    // publisher.name 形式。組み込みは builtin.<name>。
    public required string PresetId { get; init; }
    public required string Version { get; init; }
    public required string DisplayNameJa { get; init; }
    public required string DescriptionJa { get; init; }
    public required List<string> SupportedTargets { get; init; }
    // 0〜1 の目標特徴。キーは TexturePresetTraits.Names に限る。根拠が得られるまで合否には使わない(D-00)。
    public required Dictionary<string, double> Traits { get; init; }
    public required List<string> RequiredCapabilityTags { get; init; }
    public required List<string> PreferredStrategyTags { get; init; }
}

public static class TexturePresetTraits
{
    public static readonly IReadOnlyList<string> Names =
        ["syntheticness", "expressiveness", "breathiness", "tension", "articulation"];
}

public static class TexturePresetStatus
{
    public const string Available = "available";
    public const string Partial = "partial";
    public const string Unavailable = "unavailable";
    public const string Unsupported = "unsupported";
    public const string Invalid = "invalid";
    public const string Disabled = "disabled";
}

public static class TexturePresetSource
{
    public const string BuiltIn = "builtin";
    public const string Mod = "mod";
}
