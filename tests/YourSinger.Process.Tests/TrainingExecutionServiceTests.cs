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
        SchemaVersion = "1",
        SpeakerId = "spk_a",
        DatasetFingerprint = "fp",
        CalculationVersion = "v",
        InventoryRevision = revision,
        Speech = new SpeechInventory
        {
            RawSeconds = speech,
            EligibleSeconds = speech,
            DistinctRecordings = speech > 0 ? 3 : 0,
            IndependentUtterances = (int)(speech / 3),
            TranscriptUsableCount = (int)(speech / 3),
            PhonemeCoverageCount = 30
        },
        Singing = new SingingInventory
        {
            RawSeconds = singing,
            EligibleSeconds = singing,
            DistinctRecordings = singing > 0 ? 3 : 0,
            IndependentPhrases = (int)(singing / 5),
            PitchBinsObserved = singing > 0 ? 4 : 0
        },
        IdentityReference = new IdentityReferenceInventory { EligibleSeconds = speech + singing, DistinctSources = 3 },
        MeasurementStatus = new() { ["duration"] = ConfidenceStatus.Measured },
        DerivedTags = []
    };

    private static TrainingJobRecord Job(TrainingTarget target, TrainingMode mode = TrainingMode.Auto, string preset = "natural") => new()
    {
        JobId = "job-1",
        SpeakerId = "spk_a",
        Target = target,
        DatasetFingerprint = "fp",
        SegmentIds = ["s1"],
        Backend = target == TrainingTarget.Talk ? TrainingBackendCatalog.TalkBackend : TrainingBackendCatalog.SingingBackend,
        BackendRevision = target == TrainingTarget.Talk ? TrainingBackendCatalog.TalkRevision : TrainingBackendCatalog.SingingRevision,
        RequestedTexture = new TrainingTextureRequest { Preset = preset },
        RequestedTrainingMode = mode,
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

// {
//   責務: [TrainerExecutionBindingTests: 各TrainerServiceの実行処理が、計画の無いjobを学習前に止めることを確認する]
// }
public sealed class TrainerExecutionBindingTests
{
    [Fact]
    public async Task BothTrainersRefuseAJobWithoutAResolvedPlanBeforeCallingTheWorker()
    {
        using var project = new TestProject();
        var worker = new YourSinger.Process.Processing.Ml.Bridge.MlWorkerClient("worker-that-must-not-start", []);
        var singing = new TrainingJobRecord
        {
            JobId = "j",
            SpeakerId = "spk_a",
            Target = TrainingTarget.Singing,
            DatasetFingerprint = "fp",
            SegmentIds = ["s"],
            Backend = TrainingBackendCatalog.SingingBackend,
            BackendRevision = TrainingBackendCatalog.SingingRevision
        };
        var talk = new TrainingJobRecord
        {
            JobId = "j",
            SpeakerId = "spk_a",
            Target = TrainingTarget.Talk,
            DatasetFingerprint = "fp",
            SegmentIds = ["s"],
            Backend = TrainingBackendCatalog.TalkBackend,
            BackendRevision = TrainingBackendCatalog.TalkRevision
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new YourSinger.Process.Processing.Singing.DiffSingerTrainerService(worker)
                .AsExecution(project.Workspace, "config.yaml")(singing, TestProject.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new YourSinger.Process.Processing.Talk.StyleBertVits2TrainerService(worker)
                .AsExecution(project.Workspace, "model")(talk, TestProject.Token));
    }
}

// { 責務: [SpeakerInventoryProviderTests: 保存済みデータから話者の棚卸しを作り、分類が無ければ拒否することを確認する] }
public sealed class SpeakerInventoryProviderTests
{
    [Fact]
    public async Task InventoryComesFromSavedSegmentsAndTheJobSpeaker()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("a", duration: 3), project.Segment("b", duration: 3));
        var job = Assert.Single((await project.Planner().CreateBatchAsync(project.Workspace,
            [new SpeakerTrainingSelection { SpeakerId = "spk_a", Target = TrainingTarget.Talk, AutoCorrectionEnabled = false }],
            TestProject.Token)).Jobs);
        var inventory = await new SpeakerInventoryProvider(project.Correction(), project.Classifications)
            .BuildAsync(project.Workspace, job, TestProject.Token);
        Assert.Equal("spk_a", inventory.SpeakerId);
        Assert.Equal(job.DatasetFingerprint, inventory.DatasetFingerprint);
        Assert.True(inventory.Speech.RawSeconds > 0);
        Assert.False(string.IsNullOrEmpty(inventory.InventoryRevision));
    }

    [Fact]
    public async Task MissingClassificationIsRejectedInsteadOfAnEmptyInventory()
    {
        using var project = new TestProject();
        var job = new TrainingJobRecord
        {
            JobId = "j",
            SpeakerId = "spk_a",
            Target = TrainingTarget.Talk,
            DatasetFingerprint = "fp",
            SegmentIds = ["s"],
            Backend = TrainingBackendCatalog.TalkBackend,
            BackendRevision = TrainingBackendCatalog.TalkRevision
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SpeakerInventoryProvider(project.Correction(), project.Classifications).BuildAsync(project.Workspace, job, TestProject.Token));
    }
}

// { 責務: [TalkTrainingPipelineTests: 実行不可のとき学習入力もTrainerも作られず、jobがPendingのままであることを確認する] }
public sealed class TalkTrainingPipelineTests
{
    [Fact]
    public async Task WithoutCudaNothingIsBuiltOrStartedAndTheJobStaysPending()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("a", duration: 3), project.Segment("b", duration: 3));
        var batch = await project.Planner().CreateBatchAsync(project.Workspace,
            [new SpeakerTrainingSelection { SpeakerId = "spk_a", Target = TrainingTarget.Talk, AutoCorrectionEnabled = false }],
            TestProject.Token);
        var job = Assert.Single(batch.Jobs);
        var preflight = new TrainingPreflightService(
            new TalkTrainingStrategyPlanner(BuiltInTalkStrategies.CreateRegistry(), new TexturePresetRegistry()),
            new SingingTrainingStrategyPlanner(BuiltInSingingStrategies.CreateRegistry(), new TexturePresetRegistry()), project.Jobs);
        var worker = new YourSinger.Process.Processing.Ml.Bridge.MlWorkerClient("worker-that-must-not-start", []);
        var pipeline = new YourSinger.Process.Processing.Talk.TalkTrainingPipeline(
            new SpeakerInventoryProvider(project.Correction(), project.Classifications),
            new TrainingExecutionService(preflight, new TrainingJobRunner(project.Planner()), project.Jobs),
            new YourSinger.Process.Processing.Talk.StyleBertVits2DatasetBuilder(project.DatasetRepository),
            new YourSinger.Process.Processing.Talk.StyleBertVits2TrainerService(worker), project.Jobs,
            _ => Task.FromResult(new HardwareInfo(false, null)),
            _ => Task.FromResult<IReadOnlyList<ResolverCheckpoint>>([]));
        var outcome = await pipeline.RunAsync(project.Workspace, batch.BatchId, job.JobId, "trainer", "lock", TestProject.Token);
        Assert.Null(outcome.State);
        Assert.NotEmpty(outcome.StopReasons);
        TestProject.AssertNoTrainingInputPublished(project.Workspace);
        var saved = (await project.Jobs.LoadBatchAsync(project.Workspace, batch.BatchId, TestProject.Token))!.Jobs.Single();
        Assert.Equal(TrainingJobState.Pending, saved.State);
    }
}
