using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// Preflight が解決結果をジョブに固定し、実行不可・棚卸しの変化を学習前に止めることを確認する。実際の学習は対象外。
public sealed class TrainingPreflightServiceTests
{
    private static SpeakerDataInventory Inventory(double speech, double singing, string revision = "rev") => new()
    {
        SchemaVersion = "1", SpeakerId = "spk_a", DatasetFingerprint = "fp", CalculationVersion = "v", InventoryRevision = revision,
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

    private static TrainingJobRecord Job(TrainingTarget target, TrainingMode mode = TrainingMode.Auto, string preset = "natural") => new()
    {
        JobId = "job-1", SpeakerId = "spk_a", Target = target, DatasetFingerprint = "fp", SegmentIds = ["s1"],
        Backend = target == TrainingTarget.Talk ? TrainingBackendCatalog.TalkBackend : TrainingBackendCatalog.SingingBackend,
        BackendRevision = target == TrainingTarget.Talk ? TrainingBackendCatalog.TalkRevision : TrainingBackendCatalog.SingingRevision,
        RequestedTexture = new TrainingTextureRequest { Preset = preset }, RequestedTrainingMode = mode,
        ResolvedTrainingMode = mode == TrainingMode.Auto ? null : mode,
        Initialization = mode == TrainingMode.FromScratch ? new TrainingInitialization { Type = TrainingJobVocabulary.RandomInitialization } : null
    };

    private static ResolverCheckpoint[] TalkCheckpoints() =>
    [
        .. new[] { "G_0.safetensors", "D_0.safetensors", "DUR_0.safetensors" }.Select(name =>
            new ResolverCheckpoint(name, $"pretrained/{name}", "aa", "aa", true, TrainingBackendCatalog.TalkRevision))
    ];

    private static TrainingPreflightService Service(TestProject project) => new(
        new TalkTrainingStrategyPlanner(BuiltInTalkStrategies.CreateRegistry(), new TexturePresetRegistry()),
        new SingingTrainingStrategyPlanner(BuiltInSingingStrategies.CreateRegistry(), new TexturePresetRegistry()), project.Jobs);

    private static async Task<TrainingJobBatch> Store(TestProject project, TrainingJobRecord job)
    {
        var batch = new TrainingJobBatch { BatchId = "batch-1", InputVersion = TrainingJobPlanningService.CurrentInputVersion, Jobs = [job] };
        await project.Jobs.CreateBatchAsync(project.Workspace, batch, TestProject.Token);
        return batch;
    }

    private static readonly PreflightEnvironment Ready = new(new HardwareInfo(true, 12000), TalkCheckpoints());

    [Fact]
    public async Task Executable_PlanIsSavedOnTheJob_AndRoundTrips()
    {
        using var project = new TestProject();
        await Store(project, Job(TrainingTarget.Talk));
        var record = await Service(project).PreflightAsync(project.Workspace, "batch-1", "job-1", Inventory(900, 0), Ready, TestProject.Token);
        Assert.True(record.Executable);
        Assert.Equal("fine_tune", record.ResolvedTrainingMode);
        var loaded = (await project.Jobs.LoadBatchAsync(project.Workspace, "batch-1", TestProject.Token))!.Jobs.Single();
        Assert.Equal(record.ManifestHash, loaded.ResolvedPlan!.ManifestHash);
        Assert.Equal(3, loaded.ResolvedPlan.Stages.Single(x => x.OperationId == "builtin.sbv2.train").Initialization!.Components.Count);
        TrainingPreflightService.RequireExecutable(loaded, "rev");
    }

    [Fact]
    public async Task NotExecutable_IsSavedWithReasons_AndRequireExecutableStopsTheJob()
    {
        using var project = new TestProject();
        await Store(project, Job(TrainingTarget.Talk));
        var record = await Service(project).PreflightAsync(project.Workspace, "batch-1", "job-1", Inventory(900, 0),
            new PreflightEnvironment(new HardwareInfo(false, null), TalkCheckpoints()), TestProject.Token);
        Assert.False(record.Executable);
        Assert.Contains(record.StopReasons, x => x.Contains("CUDA"));
        var job = (await project.Jobs.LoadBatchAsync(project.Workspace, "batch-1", TestProject.Token))!.Jobs.Single();
        var error = Assert.Throws<InvalidOperationException>(() => TrainingPreflightService.RequireExecutable(job, "rev"));
        Assert.Contains("CUDA", error.Message);
    }

    [Fact]
    public async Task NoCandidate_KeepsThePlannerReasons()
    {
        using var project = new TestProject();
        await Store(project, Job(TrainingTarget.Talk));
        var record = await Service(project).PreflightAsync(project.Workspace, "batch-1", "job-1", Inventory(5, 5), Ready, TestProject.Token);
        Assert.False(record.Executable);
        Assert.NotEmpty(record.StopReasons);
        Assert.All(record.StopReasons, x => Assert.StartsWith("builtin.", x));
    }

    [Fact]
    public async Task ChangedInventory_AfterPreflight_RequiresPreflightAgain()
    {
        using var project = new TestProject();
        await Store(project, Job(TrainingTarget.Talk));
        await Service(project).PreflightAsync(project.Workspace, "batch-1", "job-1", Inventory(900, 0), Ready, TestProject.Token);
        var job = (await project.Jobs.LoadBatchAsync(project.Workspace, "batch-1", TestProject.Token))!.Jobs.Single();
        Assert.Throws<InvalidOperationException>(() => TrainingPreflightService.RequireExecutable(job, "rev-changed"));
        Assert.Throws<InvalidOperationException>(() => TrainingPreflightService.RequireExecutable(Job(TrainingTarget.Talk), "rev"));
    }

    [Fact]
    public async Task LegacyJobsOtherSpeakersAndStartedJobs_AreRejected()
    {
        using var project = new TestProject();
        var legacy = new TrainingJobRecord { JobId = "job-1", SpeakerId = "spk_a", Target = TrainingTarget.Talk, DatasetFingerprint = "fp", SegmentIds = ["s1"] };
        await Store(project, legacy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(project)
            .PreflightAsync(project.Workspace, "batch-1", "job-1", Inventory(900, 0), Ready, TestProject.Token));

        using var other = new TestProject();
        await Store(other, Job(TrainingTarget.Talk));
        var inventory = Inventory(900, 0);
        var wrongSpeaker = new SpeakerDataInventory
        {
            SchemaVersion = inventory.SchemaVersion, SpeakerId = "spk_b", DatasetFingerprint = inventory.DatasetFingerprint,
            CalculationVersion = inventory.CalculationVersion, InventoryRevision = inventory.InventoryRevision, Speech = inventory.Speech,
            Singing = inventory.Singing, IdentityReference = inventory.IdentityReference, MeasurementStatus = inventory.MeasurementStatus,
            DerivedTags = inventory.DerivedTags
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(other)
            .PreflightAsync(other.Workspace, "batch-1", "job-1", wrongSpeaker, Ready, TestProject.Token));

        using var started = new TestProject();
        var running = Job(TrainingTarget.Talk);
        running.State = TrainingJobState.Running;
        await Store(started, running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(started)
            .PreflightAsync(started.Workspace, "batch-1", "job-1", Inventory(900, 0), Ready, TestProject.Token));
    }

    [Fact]
    public async Task SingingJob_UsesTheSingingPlanner_AndTheTargetStaysSinging()
    {
        using var project = new TestProject();
        await Store(project, Job(TrainingTarget.Singing, TrainingMode.FromScratch));
        var record = await Service(project).PreflightAsync(project.Workspace, "batch-1", "job-1", Inventory(0, 3000), Ready, TestProject.Token);
        Assert.True(record.Executable);
        Assert.Equal(BuiltInSingingStrategies.SingingTeacherId, record.StrategyId);
        Assert.Equal("from_scratch", record.ResolvedTrainingMode);
    }
}
