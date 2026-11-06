using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// トーク学習工程案の構成を確認する。実際の学習・品質評価は対象外(候補の構成のみ)。
public sealed class TalkTrainingStrategyPlannerTests
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
            IndependentPhrases = (int)(singing / 5), PitchBinsObserved = 4
        },
        IdentityReference = new IdentityReferenceInventory { EligibleSeconds = speech + singing, DistinctSources = 3 },
        MeasurementStatus = new() { ["duration"] = ConfidenceStatus.Measured }, DerivedTags = []
    };

    private static TalkTrainingStrategyPlanner Planner(IReadOnlySet<string>? capabilities = null) =>
        new(BuiltInTalkStrategies.CreateRegistry(capabilities), new TexturePresetRegistry());

    private static TalkPlanRequest Request(SpeakerDataInventory inventory, string preset = "builtin.natural", params TalkReferenceSegment[] references) =>
        new("spk_a", preset, null, inventory, references);

    [Fact]
    public void SingingOnly_TalkWithSyntheticTexture_ProducesTheSingingTimbreCandidate()
    {
        var plan = Planner().Plan(Request(Inventory(0, 600), "builtin.classic_synthetic"));
        Assert.Equal("talk", plan.Target);
        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal("singing_timbre_reference", candidate.Variant);
        Assert.Equal(BuiltInTalkStrategies.SingingTimbreId, candidate.Reference.StrategyId);
        Assert.Contains(plan.Warnings, x => x.Contains("発話教師がありません"));
        Assert.Contains(plan.Rejected, x => x.StrategyId == BuiltInTalkStrategies.SpeechTeacherId);
    }

    [Fact]
    public void SpeechRich_ProducesTheSpeechTeacherCandidateFirst()
    {
        var plan = Planner().Plan(Request(Inventory(900, 600)));
        Assert.Equal("speech_teacher_based", plan.Candidates[0].Variant);
        Assert.Contains(plan.Candidates, x => x.Variant == "singing_timbre_reference");
        Assert.Equal("rev", plan.Candidates[0].Reference.InventoryRevision);
    }

    [Fact]
    public void Texture_ChangesTheCandidates_AndUnimplementedStyleConditioningIsRejectedWithReason()
    {
        var plain = Planner().Plan(Request(Inventory(900, 0), "builtin.classic_synthetic"));
        Assert.DoesNotContain(plain.Candidates, x => x.Reference.StrategyId == BuiltInTalkStrategies.StyleAdaptationId);

        var expressiveDefault = Planner().Plan(Request(Inventory(900, 0), "builtin.expressive_complex"));
        Assert.DoesNotContain(expressiveDefault.Candidates, x => x.Reference.StrategyId == BuiltInTalkStrategies.StyleAdaptationId);
        var reason = Assert.Single(expressiveDefault.Rejected, x => x.StrategyId == BuiltInTalkStrategies.StyleAdaptationId);
        Assert.Contains(reason.Reasons, x => x.Contains("style_conditioning"));

        var capable = new HashSet<string>([.. BuiltInTalkStrategies.AvailableCapabilities, "style_conditioning"]);
        var expressive = Planner(capable).Plan(Request(Inventory(900, 0), "builtin.expressive_complex"));
        Assert.Contains(expressive.Candidates, x => x.Reference.StrategyId == BuiltInTalkStrategies.StyleAdaptationId);
        Assert.Equal(["speech_base", "talk_style_adaptation", "evaluate_unseen_text"],
            expressive.Candidates.Single(x => x.Reference.StrategyId == BuiltInTalkStrategies.StyleAdaptationId).Stages.Select(x => x.StageId));
    }

    [Fact]
    public void NothingUsable_ReturnsNoCandidatesWithReasons_NotAFallbackToSinging()
    {
        var plan = Planner().Plan(Request(Inventory(5, 5)));
        Assert.False(plan.HasCandidates);
        Assert.Equal("talk", plan.Target);
        Assert.All(plan.Rejected, x => Assert.NotEmpty(x.Reasons));
    }

    [Fact]
    public void ReferenceSegments_MustBeTheSameSpeaker_AndAreKeptSorted()
    {
        Assert.Throws<ArgumentException>(() => Planner().Plan(Request(Inventory(900, 0), "builtin.natural",
            new TalkReferenceSegment("s1", "spk_b"))));
        var plan = Planner().Plan(Request(Inventory(900, 0), "builtin.reference_match",
            new TalkReferenceSegment("s2", "spk_a"), new TalkReferenceSegment("s1", "spk_a"), new TalkReferenceSegment("s2", "spk_a")));
        Assert.All(plan.Candidates, x => Assert.Equal(["s1", "s2"], x.ReferenceSegmentIds));
    }

    [Fact]
    public void ReferenceMatch_WithoutReferences_FailsExplicitly()
    {
        Assert.Throws<ArgumentException>(() => Planner().Plan(Request(Inventory(900, 0), "builtin.reference_match")));
    }

    [Fact]
    public void WrongSpeakerInventory_UnknownPreset_AndEmptySpeaker_FailExplicitly()
    {
        Assert.Throws<ArgumentException>(() => Planner().Plan(Request(Inventory(900, 0, "spk_b"))));
        Assert.Throws<ArgumentException>(() => Planner().Plan(Request(Inventory(900, 0), "acme.missing")));
        Assert.Throws<ArgumentException>(() => Planner().Plan(new TalkPlanRequest("", "builtin.natural", null, Inventory(900, 0), [])));
    }

    [Fact]
    public void ModStrategy_ThatTrainsSpeechFromSinging_IsNeverACandidate()
    {
        var operations = BuiltInTalkStrategies.CreateOperations();
        operations.Register("acme.train", ["singing_teacher"], ["talk_model", "model_artifact"]);
        var guarded = new TrainingStrategyRegistry(operations, BuiltInTalkStrategies.AvailableCapabilities);
        var accepted = guarded.RegisterMod("""
            {"schema_version":"1","strategy_id":"acme.song_as_speech","version":"1.0.0","display_name_ja":"歌を発話教師に","description_ja":"テスト",
             "supported_targets":["talk"],"supported_preset_ids":[],"requirements":[],"required_capabilities":[],
             "stages":[{"id":"train","operation_id":"acme.train","depends_on":[],"input_roles":["singing_teacher"],"output_types":["talk_model","model_artifact"]}],
             "expected_output_types":["talk_model"]}
            """, "test");
        Assert.True(accepted.Accepted);
        var plan = new TalkTrainingStrategyPlanner(guarded, new TexturePresetRegistry()).Plan(Request(Inventory(0, 600)));
        Assert.False(plan.HasCandidates);
        Assert.Contains(plan.Rejected.Single().Reasons, x => x.Contains("通常発話の教師"));
    }

    [Fact]
    public void PlanIsDeterministic()
    {
        var first = Planner().Plan(Request(Inventory(900, 600)));
        var second = Planner().Plan(Request(Inventory(900, 600)));
        Assert.Equal(first.Candidates.Select(x => x.Reference.Hash), second.Candidates.Select(x => x.Reference.Hash));
    }
}
