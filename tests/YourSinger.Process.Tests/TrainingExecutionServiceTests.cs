using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// {
//   責務: [TrainingExecutionServiceTests: 計画解決から終了状態の記録までの連結を確認する]
//   補足: [Trainerは代替の処理で置き換える。実Trainer・実GPUは対象外]
// }
public sealed class TrainingExecutionServiceTests
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

    private static TrainingExecutionService Execution(TestProject project) => new(
        new TrainingPreflightService(
            new TalkTrainingStrategyPlanner(BuiltInTalkStrategies.CreateRegistry(), new TexturePresetRegistry()),
            new SingingTrainingStrategyPlanner(BuiltInSingingStrategies.CreateRegistry(), new TexturePresetRegistry()), project.Jobs),
        new TrainingJobRunner(project.Planner()), project.Jobs);

    private static async Task StoreAsync(TestProject project)
    {
        var batch = new TrainingJobBatch { BatchId = "batch-1", InputVersion = TrainingJobPlanningService.CurrentInputVersion, Jobs = [Job(TrainingTarget.Talk)] };
        await project.Jobs.CreateBatchAsync(project.Workspace, batch, TestProject.Token);
    }

    private static async Task<TrainingJobRecord> LoadAsync(TestProject project) =>
        (await project.Jobs.LoadBatchAsync(project.Workspace, "batch-1", TestProject.Token))!.Jobs.Single();

    [Fact]
    public async Task AnExecutablePlanRunsTheTrainerOnTheSavedJobAndRecordsSuccess()
    {
        using var project = new TestProject();
        await StoreAsync(project);
        TrainingJobRecord? seen = null;
        var outcome = await Execution(project).ExecuteAsync(project.Workspace, "batch-1", "job-1", Inventory(900, 0), Ready,
            (job, _) => { seen = job; return Task.FromResult("log"); }, TestProject.Token);
        Assert.Equal(TrainingJobState.Succeeded, outcome.State);
        Assert.NotNull(seen!.ResolvedPlan);
        Assert.Equal(TrainingJobState.Succeeded, (await LoadAsync(project)).State);
    }

    [Fact]
    public async Task ANonExecutablePlanNeverStartsTheTrainerAndKeepsTheJobPending()
    {
        using var project = new TestProject();
        await StoreAsync(project);
        var started = false;
        var outcome = await Execution(project).ExecuteAsync(project.Workspace, "batch-1", "job-1", Inventory(900, 0),
            new PreflightEnvironment(new HardwareInfo(false, null), TalkCheckpoints()),
            (_, _) => { started = true; return Task.FromResult("log"); }, TestProject.Token);
        Assert.Null(outcome.State);
        Assert.Contains(outcome.StopReasons, x => x.Contains("CUDA"));
        Assert.False(started);
        Assert.Equal(TrainingJobState.Pending, (await LoadAsync(project)).State);
    }

    [Fact]
    public async Task ATrainerFailureIsRecordedAsFailedWithItsReason()
    {
        using var project = new TestProject();
        await StoreAsync(project);
        var outcome = await Execution(project).ExecuteAsync(project.Workspace, "batch-1", "job-1", Inventory(900, 0), Ready,
            (_, _) => throw new InvalidOperationException("終了コード7"), TestProject.Token);
        Assert.Equal(TrainingJobState.Failed, outcome.State);
        Assert.Contains("終了コード7", (await LoadAsync(project)).ErrorMessage);
    }
}
