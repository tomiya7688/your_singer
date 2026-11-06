using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// プランナー候補のモード・初期重み・GPU・素材条件の解決を確認する。実際の学習は対象外。
public sealed class TrainingModeResolverTests
{
    private const string Sha = "aa";

    private static SpeakerDataInventory Inventory(double speech, double singing) => new()
    {
        SchemaVersion = "1", SpeakerId = "spk_a", DatasetFingerprint = "fp", CalculationVersion = "v", InventoryRevision = "rev",
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

    private static readonly HardwareInfo Gpu = new(true, 12000);

    private static ResolverCheckpoint[] TalkCheckpoints(string? actual = Sha, bool exists = true, string? revision = null) =>
    [
        .. new[] { "G_0.safetensors", "D_0.safetensors", "DUR_0.safetensors" }.Select(name =>
            new ResolverCheckpoint(name, $"pretrained/{name}", Sha, actual, exists, revision ?? TrainingBackendCatalog.TalkRevision))
    ];

    private static ResolverCheckpoint[] SingingCheckpoint(string? actual = Sha) =>
        [new("acoustic", "pretrained/acoustic.ckpt", Sha, actual, true, TrainingBackendCatalog.SingingRevision)];

    private static IReadOnlySet<string> Encoder => new HashSet<string>(["diffsinger_acoustic_training", "shared_speaker_encoder"]);

    private static ResolverCandidate[] TalkCandidates(SpeakerDataInventory inventory, string preset = "builtin.natural") =>
        [.. new TalkTrainingStrategyPlanner(BuiltInTalkStrategies.CreateRegistry(), new TexturePresetRegistry())
            .Plan(new TalkPlanRequest("spk_a", preset, null, inventory, [])).Candidates.Select(ResolverCandidate.From)];

    private static ResolverCandidate[] SingingCandidates(SpeakerDataInventory inventory) =>
        [.. new SingingTrainingStrategyPlanner(BuiltInSingingStrategies.CreateRegistry(Encoder), new TexturePresetRegistry())
            .Plan(new SingingPlanRequest("spk_a", "builtin.natural", null, inventory, [])).Candidates.Select(ResolverCandidate.From)];

    private static ModeResolutionRequest Request(
        string target, SpeakerDataInventory inventory, ResolverCandidate[] candidates, TrainingMode mode = TrainingMode.Auto,
        HardwareInfo? hardware = null, ResolverCheckpoint[]? checkpoints = null, string preset = "builtin.natural") =>
        new(target, "spk_a", new TrainingTextureRequest { Preset = preset }, inventory, mode, candidates,
            hardware ?? Gpu, checkpoints ?? [], 7);

    [Fact]
    public void SingingOnly_Talk_ResolvesTheSingingTimbreCandidate_WithNoTrainerStage()
    {
        var inventory = Inventory(0, 3000);
        var plan = TrainingModeResolver.Resolve(Request("talk", inventory, TalkCandidates(inventory)));
        Assert.True(plan.Executable);
        Assert.Equal("singing_timbre_reference", plan.Chosen!.Variant);
        Assert.All(plan.Stages, x => Assert.Equal(ResolvedStageMode.NotApplicable, x.Mode));
        Assert.Equal("talk", plan.Target);
    }

    [Fact]
    public void SpeechRich_Talk_AutoUsesFineTuneWhenValidCheckpointsExist()
    {
        var inventory = Inventory(900, 600);
        var plan = TrainingModeResolver.Resolve(Request("talk", inventory, TalkCandidates(inventory), checkpoints: TalkCheckpoints()));
        Assert.True(plan.Executable);
        Assert.Equal("speech_teacher_based", plan.Chosen!.Variant);
        var train = plan.Stages.Single(x => x.OperationId == "builtin.sbv2.train");
        Assert.Equal(ResolvedStageMode.FineTune, train.Mode);
        Assert.Equal(TrainingJobVocabulary.PretrainedCheckpoint, train.Initialization!.Type);
        Assert.Equal(3, train.Initialization.Components.Count);
        Assert.Equal(ResolvedStageMode.FineTune, plan.ResolvedTrainingMode);
    }

    [Fact]
    public void Auto_WithoutUsableCheckpoints_StopsForSbv2_ButFallsBackToScratchWithAWarningForDiffSinger()
    {
        var talk = Inventory(900, 0);
        var stopped = TrainingModeResolver.Resolve(Request("talk", talk, TalkCandidates(talk)));
        Assert.False(stopped.Executable);
        Assert.Contains(stopped.Rejected.SelectMany(x => x.Reasons), x => x.Contains("自動では決められません"));

        var singing = Inventory(0, 3000);
        var plan = TrainingModeResolver.Resolve(Request("singing", singing, SingingCandidates(singing)));
        Assert.True(plan.Executable);
        var acoustic = plan.Stages.Single(x => x.OperationId == "builtin.diffsinger.acoustic");
        Assert.Equal(ResolvedStageMode.FromScratch, acoustic.Mode);
        Assert.Equal(TrainingJobVocabulary.RandomInitialization, acoustic.Initialization!.Type);
        Assert.Empty(acoustic.Initialization.Components);
        Assert.Contains(plan.Warnings, x => x.Contains("新規学習にします"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("revision")]
    public void FineTune_FailsOnMissingMismatchedOrIncompatibleCheckpoints(string kind)
    {
        var inventory = Inventory(900, 0);
        var checkpoints = kind switch
        {
            "missing" => TalkCheckpoints(exists: false),
            "hash" => TalkCheckpoints(actual: "bb"),
            _ => TalkCheckpoints(revision: "other")
        };
        var plan = TrainingModeResolver.Resolve(Request("talk", inventory, TalkCandidates(inventory), TrainingMode.FineTune, checkpoints: checkpoints));
        Assert.False(plan.Executable);
        Assert.NotEmpty(plan.Rejected.SelectMany(x => x.Reasons));
    }

    [Fact]
    public void FromScratch_IsRejectedForSbv2_AndWhenBaseCheckpointsAreSupplied()
    {
        var talk = Inventory(900, 0);
        var sbv2 = TrainingModeResolver.Resolve(Request("talk", talk, TalkCandidates(talk), TrainingMode.FromScratch));
        Assert.Contains(sbv2.Rejected.SelectMany(x => x.Reasons), x => x.Contains("from_scratch"));

        var singing = Inventory(0, 3000);
        var supplied = TrainingModeResolver.Resolve(Request("singing", singing, SingingCandidates(singing), TrainingMode.FromScratch,
            checkpoints: SingingCheckpoint()));
        Assert.False(supplied.Executable);
        Assert.Contains(supplied.Rejected.SelectMany(x => x.Reasons), x => x.Contains("初期重みを指定できません"));
        var ok = TrainingModeResolver.Resolve(Request("singing", singing, SingingCandidates(singing), TrainingMode.FromScratch));
        Assert.Equal(ResolvedStageMode.FromScratch, ok.ResolvedTrainingMode);
    }

    [Fact]
    public void NoCudaOrTooLittleVram_StopsBeforeTraining()
    {
        var inventory = Inventory(900, 0);
        var noGpu = TrainingModeResolver.Resolve(Request("talk", inventory, TalkCandidates(inventory),
            hardware: new HardwareInfo(false, null), checkpoints: TalkCheckpoints()));
        Assert.False(noGpu.Executable);
        Assert.Contains(noGpu.Rejected.SelectMany(x => x.Reasons), x => x.Contains("CUDA"));

        var unknownVram = TrainingModeResolver.Resolve(Request("talk", inventory, TalkCandidates(inventory),
            hardware: new HardwareInfo(true, null), checkpoints: TalkCheckpoints()));
        Assert.True(unknownVram.Executable);
        Assert.Contains(unknownVram.Warnings, x => x.Contains("GPUメモリ量を測定できていません"));
    }

    [Fact]
    public void EmptyTeacherData_StopsTheTrainerStage()
    {
        var withData = Inventory(900, 0);
        var empty = Inventory(0, 0);
        var plan = TrainingModeResolver.Resolve(Request("talk", empty, TalkCandidates(withData), checkpoints: TalkCheckpoints()));
        Assert.False(plan.Executable);
        Assert.Contains(plan.Rejected.SelectMany(x => x.Reasons), x => x.Contains("発話教師が空"));
    }

    [Fact]
    public void TargetIsNeverChanged_ACandidateForAnotherTargetFails()
    {
        var inventory = Inventory(900, 3000);
        Assert.Throws<ArgumentException>(() =>
            TrainingModeResolver.Resolve(Request("singing", inventory, TalkCandidates(inventory))));
    }

    [Fact]
    public void SpeechRich_Singing_AndTheTwoTargetsAreIndependent()
    {
        var inventory = Inventory(2000, 0);
        var singing = TrainingModeResolver.Resolve(Request("singing", inventory, SingingCandidates(inventory)));
        Assert.True(singing.Executable);
        Assert.Equal("speech_timbre_reference", singing.Chosen!.Variant);
        var talk = TrainingModeResolver.Resolve(Request("talk", inventory, TalkCandidates(inventory), checkpoints: TalkCheckpoints()));
        Assert.NotEqual(singing.ManifestHash, talk.ManifestHash);
    }

    [Fact]
    public void ManifestHash_ChangesWithTheTexture_AndIsStableOtherwise()
    {
        var inventory = Inventory(900, 0);
        var natural = TrainingModeResolver.Resolve(Request("talk", inventory, TalkCandidates(inventory), checkpoints: TalkCheckpoints()));
        var again = TrainingModeResolver.Resolve(Request("talk", inventory, TalkCandidates(inventory), checkpoints: TalkCheckpoints()));
        var synthetic = TrainingModeResolver.Resolve(Request("talk", inventory, TalkCandidates(inventory, "builtin.classic_synthetic"),
            checkpoints: TalkCheckpoints(), preset: "builtin.classic_synthetic"));
        Assert.Equal(natural.ManifestHash, again.ManifestHash);
        Assert.NotEqual(natural.ManifestHash, synthetic.ManifestHash);
        Assert.Equal("builtin.classic_synthetic", synthetic.Texture.Preset);
    }

    [Fact]
    public void NoCandidates_IsNotExecutable()
    {
        var inventory = Inventory(5, 5);
        var plan = TrainingModeResolver.Resolve(Request("talk", inventory, TalkCandidates(inventory)));
        Assert.False(plan.Executable);
        Assert.Null(plan.ResolvedTrainingMode);
    }
}
