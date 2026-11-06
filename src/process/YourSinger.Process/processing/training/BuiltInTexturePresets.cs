using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// 組み込みの質感プリセット8件。正本: docs/design/v1-training/texture-preset-taxonomy.md。
// traits の値は方向を示す初期値で、根拠が得られるまで保存・表示するだけで合否には使わない(D-00)。
public static class BuiltInTexturePresets
{
    private static readonly string[] Both = ["talk", "singing"];

    private static TexturePresetRecord Make(
        string name, string displayNameJa, string descriptionJa, Dictionary<string, double> traits,
        string[]? capabilityTags = null, string[]? targets = null) => new()
    {
        SchemaVersion = TexturePresetRecord.CurrentSchemaVersion, PresetId = $"builtin.{name}", Version = "1.0.0",
        DisplayNameJa = displayNameJa, DescriptionJa = descriptionJa, SupportedTargets = [.. targets ?? Both],
        Traits = traits, RequiredCapabilityTags = [.. capabilityTags ?? []], PreferredStrategyTags = []
    };

    public static readonly IReadOnlyList<TexturePresetRecord> All =
    [
        Make("classic_synthetic", "クラシック合成", "旧来の電子的で規則的な質感。正確な音素と音符、制御されたF0を重視します。",
            new() { ["syntheticness"] = 0.9, ["articulation"] = 0.8 }, ["controlled_f0"]),
        Make("clean_controlled", "クリア・安定", "明瞭で安定した発音とF0・時間長の精度を優先します。",
            new() { ["articulation"] = 0.9, ["expressiveness"] = 0.3 }),
        Make("natural", "自然", "本人らしい声色と自然な抑揚・遷移を保ちます。",
            new() { ["syntheticness"] = 0.1, ["expressiveness"] = 0.5 }),
        Make("expressive_complex", "表現豊か", "複数の表現軸(style・tension・breath・声色など)を使う豊かな声です。",
            new() { ["expressiveness"] = 0.9 }, ["style_conditioning"]),
        Make("soft_breathy", "やわらかい息", "柔らかく息のある声です。参考例と対応する制御があるときに使います。",
            new() { ["breathiness"] = 0.8, ["tension"] = 0.2 }, ["breath_control"]),
        Make("powerful", "力強い", "張りのある力強い声です。強い発声の参考例や制御を必要とします。",
            new() { ["tension"] = 0.8, ["expressiveness"] = 0.7 }, ["tension_control"]),
        Make("characterized", "キャラクター性", "声のキャラクター性を強調する質感です。",
            new() { ["expressiveness"] = 0.7 }, ["style_conditioning"]),
        Make("reference_match", "参照に近づける", "指定した同一話者の参考区間に近い質感に合わせます。参考区間の指定が必要です。",
            new() { ["expressiveness"] = 0.5 }, ["same_speaker_reference"])
    ];
}
