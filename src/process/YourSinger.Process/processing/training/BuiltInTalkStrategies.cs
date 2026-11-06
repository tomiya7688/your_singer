using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [BuiltInTalkStrategies: トーク向けの組み込み学習戦略と、その operation・能力の登録を提供する]
//   補足: [
//     成立条件の数値(秒数・発話数)は暫定値。決定記録 D-02/D-03 で実測根拠が得られるまで、
//     戦略の宣言として保存・表示するだけで、品質の保証には使わない。
//     style_conditioning は SBV2 の学習工程としてはまだ実装されていないため、利用可能な能力に含めない(unsupported と表示する)。
//   ]
// }
public static class BuiltInTalkStrategies
{
    public const string SpeechTeacherId = "builtin.talk_speech_teacher";
    public const string SingingTimbreId = "builtin.talk_singing_timbre";
    public const string StyleAdaptationId = "builtin.talk_style_adaptation";

    // この版で実在する能力。SBV2 の学習・歌唱由来スタイルの追記(#100)は実装済み。表現軸の条件付けは未実装。
    public static readonly IReadOnlySet<string> AvailableCapabilities =
        new HashSet<string>(["style_bert_vits2_training", "style_vector_from_singing_audio"], StringComparer.Ordinal);

    // {
    //   責務: [CreateOperations: 組み込み戦略が使う operation を登録した OperationRegistry を作る]
    //   戻り値: [registry: 組み込みの operation を登録済みの登録簿]
    // }
    public static OperationRegistry CreateOperations()
    {
        var operations = new OperationRegistry();
        operations.Register("builtin.sbv2.train", ["speech_teacher"], ["tts_base", "talk_model", "model_artifact"]);
        operations.Register("builtin.sbv2.timbre_style", ["identity_reference", "singing_teacher"], ["speaker_identity", "talk_model", "model_artifact"]);
        operations.Register("builtin.sbv2.style_adapt", ["speech_teacher", "texture_reference"], ["talk_model", "model_artifact"]);
        operations.Register("builtin.talk.evaluate", ["speech_teacher", "identity_reference"], ["verification_report"]);
        return operations;
    }

    // {
    //   責務: [CreateRegistry: 組み込みのトーク戦略を登録した TrainingStrategyRegistry を作る]
    //   戻り値: [registry: 組み込み戦略3件を登録済みの登録簿]
    // }
    public static TrainingStrategyRegistry CreateRegistry(IReadOnlySet<string>? capabilities = null)
    {
        var registry = new TrainingStrategyRegistry(CreateOperations(), capabilities ?? AvailableCapabilities);
        foreach (var descriptor in Descriptors()) registry.RegisterBuiltIn(descriptor);
        return registry;
    }

    private static IEnumerable<TrainingStrategyDescriptor> Descriptors()
    {
        // 発話教師から話者を学習する基本形。
        yield return Make(SpeechTeacherId, "発話教師による学習", "この話者の発話を教師にして、トークモデルを学習します。",
            [Req("speech_teacher", "eligible_seconds", 60), Req("speech_teacher", "independent_utterances", 10)],
            [], ["style_bert_vits2_training"],
            [Stage("speech_base", "builtin.sbv2.train", [], ["speech_teacher"], ["tts_base", "talk_model", "model_artifact"]),
             Stage("evaluate_unseen_text", "builtin.talk.evaluate", ["speech_base"], ["speech_teacher"], ["verification_report"])]);

        // 歌唱しか無くても検討できる経路(T1)。歌唱音声は発話の正解にせず、声質参照の新しいスタイルにだけ使う。
        yield return Make(SingingTimbreId, "歌唱由来の声質によるトーク", "この話者の歌唱音声から作った声質スタイルを、トークモデルへ追加します(歌唱は発話の教師にしません)。",
            [Req("singing_teacher", "eligible_seconds", 30)],
            [], ["style_bert_vits2_training", "style_vector_from_singing_audio"],
            [Stage("speaker_timbre_adaptation", "builtin.sbv2.timbre_style", [], ["singing_teacher"], ["speaker_identity", "talk_model", "model_artifact"]),
             Stage("evaluate_unseen_text", "builtin.talk.evaluate", ["speaker_timbre_adaptation"], ["identity_reference"], ["verification_report"])]);

        // 表現軸を持つ質感向け。表現軸の条件付けが未実装の間は unsupported になる。
        yield return Make(StyleAdaptationId, "発話教師と表現軸による学習", "発話教師で基本を学習し、質感に応じた表現軸の適応を行います。",
            [Req("speech_teacher", "eligible_seconds", 300), Req("speech_teacher", "independent_utterances", 50)],
            ["builtin.expressive_complex", "builtin.characterized", "builtin.powerful", "builtin.soft_breathy"],
            ["style_bert_vits2_training", "style_conditioning"],
            [Stage("speech_base", "builtin.sbv2.train", [], ["speech_teacher"], ["tts_base", "talk_model", "model_artifact"]),
             Stage("talk_style_adaptation", "builtin.sbv2.style_adapt", ["speech_base"], ["speech_teacher", "texture_reference"], ["talk_model", "model_artifact"]),
             Stage("evaluate_unseen_text", "builtin.talk.evaluate", ["talk_style_adaptation"], ["speech_teacher"], ["verification_report"])]);
    }

    private static StrategyRequirement Req(string role, string metric, double value) =>
        new() { Role = role, Metric = metric, Op = "gte", Value = value };

    private static StrategyStage Stage(string id, string operation, string[] dependsOn, string[] inputs, string[] outputs) =>
        new() { Id = id, OperationId = operation, DependsOn = [.. dependsOn], InputRoles = [.. inputs], OutputTypes = [.. outputs] };

    private static TrainingStrategyDescriptor Make(
        string id, string name, string description, List<StrategyRequirement> requirements, string[] presets,
        string[] capabilities, List<StrategyStage> stages)
    {
        return new TrainingStrategyDescriptor
        {
            SchemaVersion = TrainingStrategyDescriptor.CurrentSchemaVersion, StrategyId = id, Version = "1.0.0",
            DisplayNameJa = name, DescriptionJa = description, SupportedTargets = ["talk"], SupportedPresetIds = [.. presets],
            Requirements = requirements, RequiredCapabilities = [.. capabilities], Stages = stages,
            ExpectedOutputTypes = ["talk_model"]
        };
    }
}
