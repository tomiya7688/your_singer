using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// 実在する operation の登録簿。manifest の宣言だけでは operation を実在とみなさない。
// 組み込みの operation は、戦略を提供する Issue(#133/#134 など)が登録する。
public sealed class OperationRegistry
{
    private readonly Dictionary<string, (IReadOnlySet<string> InputRoles, IReadOnlySet<string> OutputTypes)> _operations =
        new(StringComparer.Ordinal);

    public void Register(string operationId, IEnumerable<string> acceptedInputRoles, IEnumerable<string> outputTypes) =>
        _operations[operationId] = (acceptedInputRoles.ToHashSet(StringComparer.Ordinal), outputTypes.ToHashSet(StringComparer.Ordinal));

    public bool TryGet(string operationId, out (IReadOnlySet<string> InputRoles, IReadOnlySet<string> OutputTypes) operation) =>
        _operations.TryGetValue(operationId, out operation);
}

// strategy.json の読込・検証(schema の形式 + Stage DAG の型検査)。違反は日本語の原因付き InvalidDataException。
public static partial class TrainingStrategyValidator
{
    [GeneratedRegex(@"^[a-z][a-z0-9_-]*\.[a-z][a-z0-9_.-]*$")]
    private static partial Regex QualifiedIdPattern();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_-]*$")]
    private static partial Regex StageIdPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex TagPattern();

    private static readonly string[] Fields =
    [
        "schema_version", "strategy_id", "version", "display_name_ja", "description_ja", "supported_targets",
        "supported_preset_ids", "requirements", "required_capabilities", "stages", "expected_output_types", "resource_manifest"
    ];

    private static readonly string[] Required = [.. Fields[..^1]];

    // {
    //   責務: [Parse: strategy.json の文字列を検証して TrainingStrategyDescriptor にする]
    //   処理: [1: JSONとして読む 2: 未知の項目・必須項目を確認する 3: 各値の形式・範囲・列挙を確認する]
    //   エラー: [不正なJSON・schema版・欠落・範囲外・未知の項目のとき InvalidDataException]
    // }
    public static TrainingStrategyDescriptor Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"JSONとして読めません: {exception.Message}", exception);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("最上位がオブジェクトではありません。");
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name)) throw new InvalidDataException($"未知の項目です: {property.Name}");
            foreach (var field in Required)
                if (!root.TryGetProperty(field, out _)) throw new InvalidDataException($"必須項目がありません: {field}");
            var schema = Text(root, "schema_version", 1, 10);
            if (schema != TrainingStrategyDescriptor.CurrentSchemaVersion)
                throw new InvalidDataException($"schema_version '{schema}' には対応していません(対応: {TrainingStrategyDescriptor.CurrentSchemaVersion})。");
            var id = Text(root, "strategy_id", 5, 128);
            if (!QualifiedIdPattern().IsMatch(id)) throw new InvalidDataException($"strategy_id の形式が不正です(publisher.name 形式): {id}");
            var version = Text(root, "version", 1, 64);
            if (!VersionPattern().IsMatch(version)) throw new InvalidDataException($"version がSemVer形式ではありません: {version}");
            return new TrainingStrategyDescriptor
            {
                SchemaVersion = schema, StrategyId = id, Version = version,
                DisplayNameJa = Text(root, "display_name_ja", 1, 100), DescriptionJa = Text(root, "description_ja", 1, 1000),
                SupportedTargets = Strings(root.GetProperty("supported_targets"), "supported_targets", TrainingAssessmentVocabulary.Targets, min: 1),
                SupportedPresetIds = [.. Strings(root.GetProperty("supported_preset_ids"), "supported_preset_ids", null, 0, QualifiedIdPattern())],
                Requirements = Requirements(root.GetProperty("requirements")),
                RequiredCapabilities = Strings(root.GetProperty("required_capabilities"), "required_capabilities", null, 0, TagPattern()),
                Stages = Stages(root.GetProperty("stages")),
                ExpectedOutputTypes = Strings(root.GetProperty("expected_output_types"), "expected_output_types",
                    TrainingStrategyVocabulary.ExpectedOutputTypes, min: 1),
                ResourceManifest = root.TryGetProperty("resource_manifest", out var resource) ? Text(root, "resource_manifest", 1, 1000) : null
            };
        }
    }

    // {
    //   責務: [ValidateDag: Stage DAGを型検査する(id一意・依存の参照・循環・operation登録・入出力型・モデル生成)]
    //   エラー: [違反のとき InvalidDataException。未登録 operation は unknown_operation として拒否]
    // }
    public static void ValidateDag(TrainingStrategyDescriptor strategy, OperationRegistry operations)
    {
        var ids = strategy.Stages.Select(x => x.Id).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new InvalidDataException("Stageのidが重複しています。");
        var byId = strategy.Stages.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var stage in strategy.Stages)
        {
            foreach (var dependency in stage.DependsOn)
                if (!byId.ContainsKey(dependency)) throw new InvalidDataException($"Stage {stage.Id} が存在しないStageに依存しています: {dependency}");
            if (!operations.TryGet(stage.OperationId, out var operation))
                throw new InvalidDataException($"unknown_operation: 登録されていないoperationです: {stage.OperationId}");
            foreach (var role in stage.InputRoles)
                if (!operation.InputRoles.Contains(role))
                    throw new InvalidDataException($"Stage {stage.Id}: operation {stage.OperationId} は入力role {role} を受け付けません。");
            foreach (var output in stage.OutputTypes)
                if (!operation.OutputTypes.Contains(output))
                    throw new InvalidDataException($"Stage {stage.Id}: operation {stage.OperationId} は出力 {output} を作れません。");
        }
        DetectCycle(strategy.Stages, byId);
        var produced = strategy.Stages.SelectMany(x => x.OutputTypes).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in strategy.ExpectedOutputTypes)
            if (!produced.Contains(expected)) throw new InvalidDataException($"期待する出力 {expected} を作るStageがありません。");
        if (!produced.Contains("model_artifact"))
            throw new InvalidDataException("モデル成果物(model_artifact)を作るStageがありません。プレビュー音声だけのStageはモデル生成と呼べません。");
    }

    // 内容から決まるhash。順序の影響を受ける要素は正規化してから計算する(plan hash の入力)。
    public static string Hash(TrainingStrategyDescriptor s)
    {
        var inv = CultureInfo.InvariantCulture;
        var canonical = string.Join('\n', s.SchemaVersion, s.StrategyId, s.Version, s.DisplayNameJa, s.DescriptionJa,
            string.Join(',', s.SupportedTargets.Order(StringComparer.Ordinal)),
            string.Join(',', s.SupportedPresetIds.Order(StringComparer.Ordinal)),
            string.Join(';', s.Requirements.Select(r => $"{r.Role}|{r.Metric}|{r.Op}|{r.Value.ToString("R", inv)}").Order(StringComparer.Ordinal)),
            string.Join(',', s.RequiredCapabilities.Order(StringComparer.Ordinal)),
            string.Join(';', s.Stages.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x =>
                $"{x.Id}|{x.OperationId}|{string.Join(',', x.DependsOn.Order(StringComparer.Ordinal))}|" +
                $"{string.Join(',', x.InputRoles.Order(StringComparer.Ordinal))}|{string.Join(',', x.OutputTypes.Order(StringComparer.Ordinal))}")),
            string.Join(',', s.ExpectedOutputTypes.Order(StringComparer.Ordinal)), s.ResourceManifest ?? "none");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void DetectCycle(List<StrategyStage> stages, Dictionary<string, StrategyStage> byId)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stage in stages) Visit(stage.Id);

        void Visit(string id)
        {
            if (state.GetValueOrDefault(id) == 2) return;
            if (state.GetValueOrDefault(id) == 1) throw new InvalidDataException($"Stageの依存が循環しています: {id}");
            state[id] = 1;
            foreach (var dependency in byId[id].DependsOn) Visit(dependency);
            state[id] = 2;
        }
    }

    private static string Text(JsonElement root, string name, int min, int max)
    {
        var element = root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} は文字列である必要があります。");
        var value = element.GetString()!;
        if (value.Length < min || value.Length > max) throw new InvalidDataException($"{name} の長さが範囲外です({min}〜{max})。");
        return value;
    }

    private static string TextOf(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{label} は文字列である必要があります。");
        return element.GetString()!;
    }

    private static List<string> Strings(JsonElement element, string name, IReadOnlyList<string>? allowed, int min, Regex? pattern = null)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{name} は配列である必要があります。");
        var values = element.EnumerateArray().Select(x => TextOf(x, name)).ToList();
        if (values.Count < min) throw new InvalidDataException($"{name} は{min}件以上必要です。");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count) throw new InvalidDataException($"{name} に重複があります。");
        if (allowed is not null && values.Any(x => !allowed.Contains(x))) throw new InvalidDataException($"{name} に未対応の値があります(許可: {string.Join(", ", allowed)})。");
        if (pattern is not null && values.Any(x => !pattern.IsMatch(x))) throw new InvalidDataException($"{name} の値の形式が不正です。");
        return values;
    }

    private static List<StrategyRequirement> Requirements(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException("requirements は配列である必要があります。");
        var result = new List<StrategyRequirement>();
        foreach (var item in element.EnumerateArray())
        {
            Closed(item, "requirements", ["role", "metric", "op", "value"]);
            var role = TextOf(item.GetProperty("role"), "requirements.role");
            var metric = TextOf(item.GetProperty("metric"), "requirements.metric");
            var op = TextOf(item.GetProperty("op"), "requirements.op");
            if (!TrainingStrategyVocabulary.Roles.Contains(role)) throw new InvalidDataException($"requirements.role が不正です: {role}");
            if (!TrainingStrategyVocabulary.Metrics.Contains(metric)) throw new InvalidDataException($"requirements.metric が不正です(実装済みの測定指標のみ): {metric}");
            if (!TrainingStrategyVocabulary.Ops.Contains(op)) throw new InvalidDataException($"requirements.op が不正です: {op}");
            var valueElement = item.GetProperty("value");
            if (valueElement.ValueKind != JsonValueKind.Number || !valueElement.TryGetDouble(out var value) || !double.IsFinite(value) || value < 0)
                throw new InvalidDataException("requirements.value は0以上の数値である必要があります(式は使えません)。");
            result.Add(new StrategyRequirement { Role = role, Metric = metric, Op = op, Value = value });
        }
        return result;
    }

    private static List<StrategyStage> Stages(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            throw new InvalidDataException("stages は1件以上の配列である必要があります。");
        var result = new List<StrategyStage>();
        foreach (var item in element.EnumerateArray())
        {
            Closed(item, "stages", ["id", "operation_id", "depends_on", "input_roles", "output_types"]);
            var id = TextOf(item.GetProperty("id"), "stages.id");
            var operation = TextOf(item.GetProperty("operation_id"), "stages.operation_id");
            if (!StageIdPattern().IsMatch(id)) throw new InvalidDataException($"stages.id の形式が不正です: {id}");
            if (!QualifiedIdPattern().IsMatch(operation) || operation.Length is < 5 or > 128)
                throw new InvalidDataException($"stages.operation_id の形式が不正です: {operation}");
            result.Add(new StrategyStage
            {
                Id = id, OperationId = operation,
                DependsOn = Strings(item.GetProperty("depends_on"), "stages.depends_on", null, 0, StageIdPattern()),
                InputRoles = Strings(item.GetProperty("input_roles"), "stages.input_roles", TrainingStrategyVocabulary.Roles, 0),
                OutputTypes = Strings(item.GetProperty("output_types"), "stages.output_types", TrainingStrategyVocabulary.StageOutputTypes, 1)
            });
        }
        return result;
    }

    private static void Closed(JsonElement item, string name, string[] fields)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{name} の要素がオブジェクトではありません。");
        foreach (var property in item.EnumerateObject())
            if (!fields.Contains(property.Name)) throw new InvalidDataException($"{name} に未知の項目があります: {property.Name}");
        foreach (var field in fields)
            if (!item.TryGetProperty(field, out _)) throw new InvalidDataException($"{name} に必須項目がありません: {field}");
    }
}
