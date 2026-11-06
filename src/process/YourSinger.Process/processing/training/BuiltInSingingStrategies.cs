using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [BuiltInSingingStrategies: 歌唱向けの組み込み学習戦略と、その operation・能力・必須 submodel を提供する]
//   補足: [
//     成立条件の数値は暫定値(決定記録 D-02/D-03 待ち)。保存・表示するだけで品質の保証には使わない。
//     利用可能な能力は実装済みのものに限る: DiffSinger の音響学習入口のみ。話者エンコーダ(S1)・表現軸の条件付けは未実装で unsupported になる。
//     必須 submodel(acoustic・duration・vocoder)のうち未提供のものは、候補の MissingSubmodels として隠さず報告する。
//   ]
// }
public static class BuiltInSingingStrategies
{
    public const string SingingTeacherId = "builtin.singing_singing_teacher";
    public const string SpeechTimbreId = "builtin.singing_speech_timbre";
    public const string ExpressiveId = "builtin.singing_expressive";

    public static readonly IReadOnlyList<string> RequiredSubmodels = ["acoustic", "duration", "vocoder"];

    // 固定版の学習入口がある submodel。duration・vocoder の重みは取得・学習とも未実装(docs/diffsinger-acoustic-input.md)。
    public static readonly IReadOnlySet<string> AvailableSubmodels = new HashSet<string>(["acoustic"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> AvailableCapabilities =
        new HashSet<string>(["diffsinger_acoustic_training"], StringComparer.Ordinal);

    // {
    //   責務: [CreateOperations: 組み込み戦略が使う operation を登録した OperationRegistry を作る]
    //   戻り値: [registry: 組み込みの歌唱 operation を登録済みの登録簿]
    // }
    public static OperationRegistry CreateOperations()
    {
        var operations = new OperationRegistry();
        operations.Register("builtin.diffsinger.acoustic", ["singing_teacher"], ["singing_base", "singing_model", "model_artifact"]);
        operations.Register("builtin.diffsinger.speaker_encoder", ["speech_teacher", "identity_reference", "singing_teacher"],
            ["speaker_identity", "singing_model", "model_artifact"]);
        operations.Register("builtin.diffsinger.expression", ["singing_teacher", "texture_reference"], ["singing_model", "model_artifact"]);
        operations.Register("builtin.singing.evaluate", ["singing_teacher", "identity_reference", "speech_teacher"], ["verification_report"]);
        return operations;
    }

    // {
    //   責務: [CreateRegistry: 組み込みの歌唱戦略を登録した TrainingStrategyRegistry を作る]
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
        yield return Make(SingingTeacherId, "歌唱教師による学習", "この話者の歌唱(検証済みの音素境界と実測F0)を教師にして、歌唱モデルを学習します。",
            [Req("singing_teacher", "eligible_seconds", 300), Req("singing_teacher", "independent_phrases", 30),
             Req("singing_teacher", "pitch_bins_observed", 3)],
            [], ["diffsinger_acoustic_training"],
            [Stage("acoustic", "builtin.diffsinger.acoustic", [], ["singing_teacher"], ["singing_base", "singing_model", "model_artifact"]),
             Stage("evaluate_unseen_notes", "builtin.singing.evaluate", ["acoustic"], ["singing_teacher"], ["verification_report"])]);

        // 発話しか無くても検討する経路(S1)。発話は話者声質の参照で、未知の音域・音符の教師には使わない。
        yield return Make(SpeechTimbreId, "発話由来の声質による歌唱", "この話者の発話から作る共有の話者表現を、他話者の歌唱教師で学習した歌唱モデルへ与えます(限定パッチが必要)。",
            [Req("speech_teacher", "eligible_seconds", 60)],
            [], ["diffsinger_acoustic_training", "shared_speaker_encoder"],
            [Stage("speaker_timbre_adaptation", "builtin.diffsinger.speaker_encoder", [], ["speech_teacher", "identity_reference"],
                ["speaker_identity", "singing_model", "model_artifact"]),
             Stage("evaluate_unseen_notes", "builtin.singing.evaluate", ["speaker_timbre_adaptation"], ["identity_reference"], ["verification_report"])]);

        yield return Make(ExpressiveId, "歌唱教師と表現軸による学習", "歌唱教師で基本を学習し、質感に応じた表現条件の適応を行います。",
            [Req("singing_teacher", "eligible_seconds", 900), Req("singing_teacher", "independent_phrases", 80)],
            ["builtin.expressive_complex", "builtin.characterized", "builtin.powerful", "builtin.soft_breathy"],
            ["diffsinger_acoustic_training", "expression_conditioning"],
            [Stage("acoustic", "builtin.diffsinger.acoustic", [], ["singing_teacher"], ["singing_base", "model_artifact"]),
             Stage("expression_adaptation", "builtin.diffsinger.expression", ["acoustic"], ["singing_teacher", "texture_reference"], ["singing_model", "model_artifact"]),
             Stage("evaluate_unseen_notes", "builtin.singing.evaluate", ["expression_adaptation"], ["singing_teacher"], ["verification_report"])]);
    }

    private static StrategyRequirement Req(string role, string metric, double value) =>
        new() { Role = role, Metric = metric, Op = "gte", Value = value };

    private static StrategyStage Stage(string id, string operation, string[] dependsOn, string[] inputs, string[] outputs) =>
        new() { Id = id, OperationId = operation, DependsOn = [.. dependsOn], InputRoles = [.. inputs], OutputTypes = [.. outputs] };

    private static TrainingStrategyDescriptor Make(
        string id, string name, string description, List<StrategyRequirement> requirements, string[] presets,
        string[] capabilities, List<StrategyStage> stages) => new()
    {
        SchemaVersion = TrainingStrategyDescriptor.CurrentSchemaVersion, StrategyId = id, Version = "1.0.0",
        DisplayNameJa = name, DescriptionJa = description, SupportedTargets = ["singing"], SupportedPresetIds = [.. presets],
        Requirements = requirements, RequiredCapabilities = [.. capabilities], Stages = stages, ExpectedOutputTypes = ["singing_model"]
    };
}
