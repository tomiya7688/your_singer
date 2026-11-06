using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 学習戦略の宣言・Stage DAG・登録簿・成立条件の判定を確認する。新しい学習アルゴリズム自体は対象外。
public sealed class TrainingStrategyRegistryTests
{
    private static OperationRegistry Operations()
    {
        var registry = new OperationRegistry();
        registry.Register("test.prepare", ["speech_teacher", "singing_teacher", "identity_reference"], ["speaker_features", "tts_base"]);
        registry.Register("test.train", ["speech_teacher", "identity_reference"], ["talk_model", "model_artifact"]);
        registry.Register("test.preview", ["speech_teacher"], ["audio_preview"]);
        return registry;
    }

    private static string Json(
        string id = "acme.talk",
        string version = "1.0.0",
        string requirements = """[{"role":"speech_teacher","metric":"eligible_seconds","op":"gte","value":60}]""",
        string stages = """
            [{"id":"prepare","operation_id":"test.prepare","depends_on":[],"input_roles":["speech_teacher"],"output_types":["speaker_features"]},
             {"id":"train","operation_id":"test.train","depends_on":["prepare"],"input_roles":["speech_teacher"],"output_types":["talk_model","model_artifact"]}]
            """,
        string expected = """["talk_model"]""",
        string presets = "[]",
        string targets = """["talk"]""",
        string capabilities = "[]",
        string extra = "") => $$"""
        {
          "schema_version": "1", "strategy_id": "{{id}}", "version": "{{version}}",
          "display_name_ja": "テスト戦略", "description_ja": "テスト用の戦略",
          "supported_targets": {{targets}}, "supported_preset_ids": {{presets}}, "requirements": {{requirements}},
          "required_capabilities": {{capabilities}}, "stages": {{stages}}, "expected_output_types": {{expected}}{{extra}}
        }
        """;

    private static SpeakerDataInventory Inventory(
        double speechSeconds = 100, double singingSeconds = 0, string duration = ConfidenceStatus.Measured,
        int utterances = 10, int sources = 2, string revision = "rev1") => new()
    {
        SchemaVersion = "1", SpeakerId = "spk_a", DatasetFingerprint = "fp", CalculationVersion = "v", InventoryRevision = revision,
        Speech = new SpeechInventory
        {
            RawSeconds = speechSeconds, EligibleSeconds = speechSeconds, DistinctRecordings = sources,
            IndependentUtterances = utterances, TranscriptUsableCount = utterances, PhonemeCoverageCount = 20
        },
        Singing = new SingingInventory
        {
            RawSeconds = singingSeconds, EligibleSeconds = singingSeconds, DistinctRecordings = 1, IndependentPhrases = 5, PitchBinsObserved = 3
        },
        IdentityReference = new IdentityReferenceInventory { EligibleSeconds = speechSeconds + singingSeconds, DistinctSources = sources },
        MeasurementStatus = new() { ["duration"] = duration }, DerivedTags = []
    };

    private static TrainingStrategyRegistry Registry(params string[] capabilities) =>
        new(Operations(), new HashSet<string>(capabilities));

    [Fact]
    public void ValidManifest_IsRegisteredAsAvailable()
    {
        var result = Registry().RegisterMod(Json(), "mods/a");
        Assert.True(result.Accepted);
        Assert.Equal(TexturePresetStatus.Available, result.Entry!.Status);
        Assert.Matches("^[0-9a-f]{64}$", result.Entry.Hash);
    }

    [Theory]
    [InlineData("""[{"role":"speech_teacher","metric":"eligible_seconds","op":"gte","value":"1+1"}]""")]
    [InlineData("""[{"role":"speech_teacher","metric":"quality_score","op":"gte","value":1}]""")]
    [InlineData("""[{"role":"speech_teacher","metric":"eligible_seconds","op":"matches","value":1}]""")]
    [InlineData("""[{"role":"speech_teacher","metric":"eligible_seconds","op":"gte","value":-1}]""")]
    [InlineData("""[{"role":"speech_teacher","metric":"eligible_seconds","op":"gte","value":1,"expr":"x"}]""")]
    [InlineData("""[{"role":"boss","metric":"eligible_seconds","op":"gte","value":1}]""")]
    public void InvalidRequirements_AreRejected(string requirements)
    {
        var registry = Registry();
        Assert.False(registry.RegisterMod(Json(requirements: requirements), "bad").Accepted);
        Assert.Empty(registry.Entries);
        Assert.Single(registry.Failures);
    }

    [Fact]
    public void UnknownFieldSchemaAndIdErrors_AreRejectedWithReason()
    {
        var registry = Registry();
        Assert.Contains("未知の項目", registry.RegisterMod(Json(extra: ""","script":"x()" """), "a").Failure);
        Assert.Contains("schema_version", registry.RegisterMod(Json().Replace("\"schema_version\": \"1\"", "\"schema_version\": \"2\""), "b").Failure);
        Assert.Contains("strategy_id", registry.RegisterMod(Json(id: "NoDot"), "c").Failure);
        Assert.Contains("JSON", registry.RegisterMod("{", "d").Failure);
    }

    [Fact]
    public void StageDag_RejectsCycleMissingDependencyDuplicateAndUnknownOperation()
    {
        string Stage(string id, string op, string deps) =>
            $$"""{"id":"{{id}}","operation_id":"{{op}}","depends_on":[{{deps}}],"input_roles":["speech_teacher"],"output_types":["talk_model","model_artifact"]}""";
        var registry = Registry();
        Assert.Contains("循環", registry.RegisterMod(Json(stages: $"[{Stage("a", "test.train", "\"b\"")},{Stage("b", "test.train", "\"a\"")}]"), "1").Failure);
        Assert.Contains("存在しない", registry.RegisterMod(Json(stages: $"[{Stage("a", "test.train", "\"zzz\"")}]"), "2").Failure);
        Assert.Contains("重複", registry.RegisterMod(Json(stages: $"[{Stage("a", "test.train", "")},{Stage("a", "test.train", "")}]"), "3").Failure);
        Assert.Contains("unknown_operation", registry.RegisterMod(Json(stages: $"[{Stage("a", "evil.download", "")}]"), "4").Failure);
        Assert.Empty(registry.Entries);
    }

    [Fact]
    public void StageDag_RejectsRoleAndOutputMismatchAndPreviewOnlyModels()
    {
        var registry = Registry();
        const string badRole = """[{"id":"a","operation_id":"test.preview","depends_on":[],"input_roles":["singing_teacher"],"output_types":["audio_preview"]}]""";
        Assert.Contains("入力role", registry.RegisterMod(Json(stages: badRole, expected: """["talk_model"]"""), "1").Failure);
        const string badOutput = """[{"id":"a","operation_id":"test.preview","depends_on":[],"input_roles":["speech_teacher"],"output_types":["talk_model"]}]""";
        Assert.Contains("作れません", registry.RegisterMod(Json(stages: badOutput), "2").Failure);
        const string previewOnly = """[{"id":"a","operation_id":"test.preview","depends_on":[],"input_roles":["speech_teacher"],"output_types":["audio_preview"]}]""";
        Assert.False(registry.RegisterMod(Json(stages: previewOnly), "3").Accepted);
        Assert.Contains("期待する出力", registry.RegisterMod(Json(expected: """["singing_model"]""", targets: """["singing"]"""), "4").Failure);
    }

    [Fact]
    public void Registry_ConflictsHijackIdempotencyAndOrdering()
    {
        var registry = Registry();
        Assert.True(registry.RegisterMod(Json(), "a").Accepted);
        Assert.True(registry.RegisterMod(Json(), "a2").Accepted);
        Assert.False(registry.RegisterMod(Json(requirements: "[]"), "conflict").Accepted);
        Assert.False(registry.RegisterMod(Json(id: "builtin.hijack"), "evil").Accepted);
        foreach (var version in new[] { "1.10.0", "1.2.0" }) Assert.True(registry.RegisterMod(Json(version: version), version).Accepted);
        Assert.Equal(["1.0.0", "1.2.0", "1.10.0"], registry.Entries.Select(x => x.Strategy.Version));
        Assert.Equal("1.10.0", registry.Find("acme.talk")!.Strategy.Version);
    }

    [Fact]
    public void BuiltIn_CannotBeHijackedByMod_AndMissingCapabilityMakesItUnsupported()
    {
        var registry = Registry("have_a");
        var descriptor = TrainingStrategyValidator.Parse(Json(id: "builtin.talk_basic", capabilities: """["need_b"]"""));
        var entry = registry.RegisterBuiltIn(descriptor);
        Assert.Equal(TexturePresetStatus.Unsupported, entry.Status);
        Assert.Contains("need_b", entry.Reason);
        Assert.False(registry.RegisterMod(Json(id: "builtin.talk_basic", version: "9.0.0"), "evil").Accepted);
        Assert.Throws<ArgumentException>(() => registry.RegisterBuiltIn(TrainingStrategyValidator.Parse(Json(id: "acme.not_builtin"))));
    }

    [Fact]
    public async Task RemovingAStrategy_KeepsTheOldJobReference()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("a"));
        var batch = await project.Planner().CreateBatchAsync(project.Workspace,
            [new SpeakerTrainingSelection { SpeakerId = "spk_a", Target = TrainingTarget.Talk, AutoCorrectionEnabled = false }], TestProject.Token);
        var registry = Registry();
        var entry = registry.RegisterMod(Json(), "a").Entry!;
        var job = Assert.Single(batch.Jobs);
        job.Strategy = TrainingStrategyRegistry.ReferenceOf(entry, new TexturePresetRegistry().Find("builtin.natural"), Inventory(revision: "inv-1"));
        await project.Jobs.SaveBatchAsync(project.Workspace, batch, TestProject.Token);

        Assert.True(registry.Remove("acme.talk", "1.0.0"));
        Assert.Empty(registry.Entries);
        var loaded = Assert.Single((await project.Jobs.LoadBatchAsync(project.Workspace, batch.BatchId, TestProject.Token))!.Jobs);
        Assert.Equal("acme.talk", loaded.Strategy!.StrategyId);
        Assert.Equal(entry.Hash, loaded.Strategy.Hash);
        Assert.Equal("builtin.natural", loaded.Strategy.PresetId);
        Assert.Equal("inv-1", loaded.Strategy.InventoryRevision);
    }

    [Fact]
    public void Fit_SpeechRichSingingRichAndMixedGiveDifferentCandidates()
    {
        var registry = Registry();
        registry.RegisterMod(Json(id: "acme.talk_from_speech"), "a");
        registry.RegisterMod(Json(id: "acme.talk_from_singing",
            requirements: """[{"role":"singing_teacher","metric":"eligible_seconds","op":"gte","value":600},{"role":"identity_reference","metric":"eligible_seconds","op":"gte","value":30}]"""), "b");
        string[] Ids(SpeakerDataInventory inventory) => [.. StrategyFitEvaluator.Candidates(registry, inventory, "talk", null).Select(x => x.Entry.Strategy.StrategyId)];
        Assert.Equal(["acme.talk_from_speech"], Ids(Inventory(speechSeconds: 100, singingSeconds: 0)));
        Assert.Equal(["acme.talk_from_singing"], Ids(Inventory(speechSeconds: 10, singingSeconds: 3000)));
        Assert.Equal(["acme.talk_from_singing", "acme.talk_from_speech"], Ids(Inventory(speechSeconds: 100, singingSeconds: 3000)));
        Assert.Empty(Ids(Inventory(speechSeconds: 10, singingSeconds: 0)));
    }

    [Fact]
    public void Fit_UnknownIsNotZero_AndPartialMeasurementIsOnlyALowerBound()
    {
        var registry = Registry();
        var entry = registry.RegisterMod(Json(), "a").Entry!;
        var unknown = StrategyFitEvaluator.Evaluate(entry, Inventory(speechSeconds: 0, duration: ConfidenceStatus.Unavailable), "talk", null);
        Assert.Equal(StrategyOutcome.Unknown, unknown.Outcome);
        Assert.Null(unknown.Results.Single().Actual);
        var partialShort = StrategyFitEvaluator.Evaluate(entry, Inventory(speechSeconds: 30, duration: "partially_measured"), "talk", null);
        Assert.Equal(StrategyOutcome.Unknown, partialShort.Outcome);
        var partialEnough = StrategyFitEvaluator.Evaluate(entry, Inventory(speechSeconds: 100, duration: "partially_measured"), "talk", null);
        Assert.Equal(StrategyOutcome.Met, partialEnough.Outcome);
        var measuredShort = StrategyFitEvaluator.Evaluate(entry, Inventory(speechSeconds: 30), "talk", null);
        Assert.Equal(StrategyOutcome.Unmet, measuredShort.Outcome);
        Assert.NotEmpty(measuredShort.UnmetReasons);
    }

    [Fact]
    public void Fit_TargetPresetCapabilityAndUnsupportedMetric()
    {
        var registry = Registry();
        var general = registry.RegisterMod(Json(id: "acme.general"), "g").Entry!;
        var specific = registry.RegisterMod(Json(id: "acme.zspecific", presets: """["builtin.natural"]"""), "s").Entry!;
        var inventory = Inventory();
        Assert.Equal(StrategyOutcome.Unmet, StrategyFitEvaluator.Evaluate(general, inventory, "singing", null).Outcome);
        Assert.Equal(StrategyOutcome.Unmet, StrategyFitEvaluator.Evaluate(specific, inventory, "talk", "builtin.powerful").Outcome);
        Assert.Equal(StrategyOutcome.Met, StrategyFitEvaluator.Evaluate(specific, inventory, "talk", "builtin.natural").Outcome);
        // 質感が違っても、同じ素材でも対象外の戦略は候補から外れ、対応する戦略が先頭になる。
        var ids = StrategyFitEvaluator.Candidates(registry, inventory, "talk", "builtin.natural").Select(x => x.Entry.Strategy.StrategyId).ToArray();
        Assert.Equal(["acme.zspecific", "acme.general"], ids);
        var unsupportedMetric = registry.RegisterMod(Json(id: "acme.tex",
            requirements: """[{"role":"texture_reference","metric":"eligible_seconds","op":"gte","value":1}]"""), "t").Entry!;
        Assert.Equal(StrategyOutcome.Unknown, StrategyFitEvaluator.Evaluate(unsupportedMetric, inventory, "talk", null).Outcome);
    }

    [Fact]
    public void Hash_IsStableAndIndependentOfListOrder_ButChangesWithContent()
    {
        var a = TrainingStrategyValidator.Parse(Json(presets: """["builtin.natural","acme.warm"]"""));
        var b = TrainingStrategyValidator.Parse(Json(presets: """["acme.warm","builtin.natural"]"""));
        Assert.Equal(TrainingStrategyValidator.Hash(a), TrainingStrategyValidator.Hash(b));
        Assert.NotEqual(TrainingStrategyValidator.Hash(a), TrainingStrategyValidator.Hash(TrainingStrategyValidator.Parse(Json())));
    }
}
