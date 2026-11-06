using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 歌唱学習工程案の構成を確認する。実際の学習・品質評価は対象外(候補の構成のみ)。
public sealed class SingingTrainingStrategyPlannerTests
{
    private static SpeakerDataInventory Inventory(double speech, double singing, string speaker = "spk_a") => new()
    {
        SchemaVersion = "1", SpeakerId = speaker, DatasetFingerprint = "fp", CalculationVersion = "v", InventoryRevision = "rev",
        Speech = new SpeechInventory
        {
            RawSeconds = speech, EligibleSeconds = speech, DistinctRecordings = speech > 0 ? 3 : 0,
            IndependentUtterances = (int)(speech / 3), TranscriptUsableCount = (int)(speech / 3), PhonemeCoverageCount = 30
        },
        Singing = new SingingInventory
        {
            RawSeconds = singing, EligibleSeconds = singing, DistinctRecordings = singing > 0 ? 3 : 0,
            IndependentPhrases = (int)(singing / 5), PitchBinsObserved = singing > 0 ? 4 : 0
        },
        IdentityReference = new IdentityReferenceInventory { EligibleSeconds = speech + singing, DistinctSources = 3 },
        MeasurementStatus = new() { ["duration"] = ConfidenceStatus.Measured }, DerivedTags = []
    };

    private static readonly IReadOnlySet<string> WithEncoder =
        new HashSet<string>([.. BuiltInSingingStrategies.AvailableCapabilities, "shared_speaker_encoder"]);

    private static SingingTrainingStrategyPlanner Planner(IReadOnlySet<string>? capabilities = null) =>
        new(BuiltInSingingStrategies.CreateRegistry(capabilities), new TexturePresetRegistry());

    private static SingingPlanRequest Request(SpeakerDataInventory inventory, string preset = "builtin.natural",
        IReadOnlySet<string>? submodels = null, params SingingReferenceSegment[] references) =>
        new("spk_a", preset, null, inventory, references, submodels);

    [Fact]
    public void SpeechOnly_Singing_IsPlannedWhenTheEncoderExists_AndNeverBecomesTalk()
    {
        var plan = Planner(WithEncoder).Plan(Request(Inventory(600, 0)));
        Assert.Equal("singing", plan.Target);
        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal("speech_timbre_reference", candidate.Variant);
        Assert.Contains(plan.Warnings, x => x.Contains("歌唱教師がありません"));
        Assert.Contains(plan.Rejected, x => x.StrategyId == BuiltInSingingStrategies.SingingTeacherId);
    }

    [Fact]
    public void SpeechOnly_WithoutTheEncoder_IsRejectedWithTheMissingCapabilityReason()
    {
        var plan = Planner().Plan(Request(Inventory(600, 0)));
        Assert.False(plan.HasCandidates);
        var reason = Assert.Single(plan.Rejected, x => x.StrategyId == BuiltInSingingStrategies.SpeechTimbreId);
        Assert.Contains(reason.Reasons, x => x.Contains("shared_speaker_encoder"));
    }

    [Fact]
    public void SingingRich_UsesTheMeasuredSingingTeacherFirst()
    {
        var plan = Planner(WithEncoder).Plan(Request(Inventory(600, 1200)));
        Assert.Equal("singing_teacher_based", plan.Candidates[0].Variant);
        Assert.Equal(BuiltInSingingStrategies.SingingTeacherId, plan.Candidates[0].Reference.StrategyId);
        Assert.Contains(plan.Candidates, x => x.Variant == "speech_timbre_reference");
    }

    [Fact]
    public void MissingSubmodels_AreReportedNotHidden()
    {
        var plan = Planner().Plan(Request(Inventory(0, 1200)));
        var candidate = Assert.Single(plan.Candidates, x => x.Variant == "singing_teacher_based");
        Assert.Equal(["duration", "vocoder"], candidate.MissingSubmodels);
        Assert.Contains(candidate.Warnings, x => x.Contains("submodel"));
        var complete = Planner().Plan(Request(Inventory(0, 1200), submodels: new HashSet<string>(["acoustic", "duration", "vocoder"])));
        Assert.Empty(complete.Candidates[0].MissingSubmodels);
    }

    [Fact]
    public void UnmeasuredMetrics_ProduceAWarning()
    {
        var inventory = Inventory(0, 1200);
        inventory.MeasurementStatus["f0"] = ConfidenceStatus.Unavailable;
        Assert.Contains(Planner().Plan(Request(inventory)).Warnings, x => x.Contains("f0"));
    }

    [Fact]
    public void Texture_ChangesThePlan()
    {
        var synthetic = Planner().Plan(Request(Inventory(0, 1200), "builtin.classic_synthetic"));
        Assert.DoesNotContain(synthetic.Candidates, x => x.Reference.StrategyId == BuiltInSingingStrategies.ExpressiveId);
        var expressiveDefault = Planner().Plan(Request(Inventory(0, 1200), "builtin.expressive_complex"));
        Assert.Contains(Assert.Single(expressiveDefault.Rejected, x => x.StrategyId == BuiltInSingingStrategies.ExpressiveId).Reasons,
            x => x.Contains("expression_conditioning"));
        var capable = new HashSet<string>([.. BuiltInSingingStrategies.AvailableCapabilities, "expression_conditioning"]);
        var expressive = Planner(capable).Plan(Request(Inventory(0, 1200), "builtin.expressive_complex"));
        Assert.Equal(["acoustic", "expression_adaptation", "evaluate_unseen_notes"],
            expressive.Candidates.Single(x => x.Reference.StrategyId == BuiltInSingingStrategies.ExpressiveId).Stages.Select(x => x.StageId));
    }

    [Fact]
    public void BadInputs_FailExplicitly()
    {
        Assert.Throws<ArgumentException>(() => Planner().Plan(Request(Inventory(0, 1200, "spk_b"))));
        Assert.Throws<ArgumentException>(() => Planner().Plan(Request(Inventory(0, 1200), "acme.missing")));
        Assert.Throws<ArgumentException>(() => Planner().Plan(new SingingPlanRequest("", "builtin.natural", null, Inventory(0, 1200), [])));
        Assert.Throws<ArgumentException>(() => Planner().Plan(Request(Inventory(0, 1200), "builtin.reference_match")));
        Assert.Throws<ArgumentException>(() => Planner().Plan(Request(Inventory(0, 1200), "builtin.natural", null,
            new SingingReferenceSegment("s1", "spk_b"))));
    }

    [Fact]
    public void ReferenceMatch_KeepsSortedDistinctReferences()
    {
        var plan = Planner().Plan(Request(Inventory(0, 1200), "builtin.reference_match", null,
            new SingingReferenceSegment("s2", "spk_a"), new SingingReferenceSegment("s1", "spk_a"), new SingingReferenceSegment("s2", "spk_a")));
        Assert.All(plan.Candidates, x => Assert.Equal(["s1", "s2"], x.ReferenceSegmentIds));
    }

    [Fact]
    public void ModStrategy_ThatBuildsTheSingingBaseFromSpeech_IsNeverACandidate()
    {
        var operations = BuiltInSingingStrategies.CreateOperations();
        operations.Register("acme.base", ["speech_teacher"], ["singing_base", "singing_model", "model_artifact"]);
        var registry = new TrainingStrategyRegistry(operations, BuiltInSingingStrategies.AvailableCapabilities);
        Assert.True(registry.RegisterMod("""
            {"schema_version":"1","strategy_id":"acme.speech_as_singing","version":"1.0.0","display_name_ja":"発話を歌唱教師に","description_ja":"テスト",
             "supported_targets":["singing"],"supported_preset_ids":[],"requirements":[],"required_capabilities":[],
             "stages":[{"id":"base","operation_id":"acme.base","depends_on":[],"input_roles":["speech_teacher"],"output_types":["singing_base","singing_model","model_artifact"]}],
             "expected_output_types":["singing_model"]}
            """, "test").Accepted);
        var plan = new SingingTrainingStrategyPlanner(registry, new TexturePresetRegistry()).Plan(Request(Inventory(600, 0)));
        Assert.False(plan.HasCandidates);
        Assert.Contains(plan.Rejected.Single().Reasons, x => x.Contains("歌唱の基礎モデル"));
    }

    [Fact]
    public void TalkAndSingingPlansForTheSameSpeakerAreIndependent()
    {
        var inventory = Inventory(900, 1200);
        var singing = Planner(WithEncoder).Plan(Request(inventory));
        var talk = new TalkTrainingStrategyPlanner(BuiltInTalkStrategies.CreateRegistry(), new TexturePresetRegistry())
            .Plan(new TalkPlanRequest("spk_a", "builtin.natural", null, inventory, []));
        Assert.Equal("singing", singing.Target);
        Assert.Equal("talk", talk.Target);
        Assert.Empty(singing.Candidates.Select(x => x.Reference.StrategyId).Intersect(talk.Candidates.Select(x => x.Reference.StrategyId)));
    }
}
