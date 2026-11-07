using YourSinger.Data.Models;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// { 責務: [SingingTrainingPipelineTests: 歌唱の初期重みの棚卸しと、実行不可時に何も作らないことを確認する] }
public sealed class SingingTrainingPipelineTests
{
    [Fact]
    public async Task WithoutARecordNoCheckpointsAreInventedAndATamperedOneIsVisible()
    {
        using var project = new TestProject();
        var directory = project.Workspace.RootPath;
        Assert.Empty(await SingingCheckpointInventory.ScanAsync(directory, TestProject.Token));

        await File.WriteAllTextAsync(Path.Combine(directory, "acoustic.ckpt"), "weights", TestProject.Token);
        await File.WriteAllTextAsync(Path.Combine(directory, SingingCheckpointInventory.ManifestName),
            "{\"backend_revision\":\"rev9\",\"files\":[{\"name\":\"acoustic\",\"file\":\"acoustic.ckpt\",\"sha256\":\"00\"}]}", TestProject.Token);
        var item = Assert.Single(await SingingCheckpointInventory.ScanAsync(directory, TestProject.Token));
        Assert.Equal(("acoustic", "rev9", true), (item.Name, item.BackendRevision, item.Exists));
        Assert.NotEqual(item.ExpectedSha256, item.ActualSha256);

        await File.WriteAllTextAsync(Path.Combine(directory, SingingCheckpointInventory.ManifestName), "{broken", TestProject.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => SingingCheckpointInventory.ScanAsync(directory, TestProject.Token));
    }

    [Fact]
    public async Task WithoutCudaNothingIsBuiltOrStartedAndTheJobStaysPending()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("a", type: SegmentContentType.Singing, duration: 3),
            project.Segment("b", type: SegmentContentType.Singing, duration: 3));
        var batch = await project.Planner().CreateBatchAsync(project.Workspace,
            [new SpeakerTrainingSelection { SpeakerId = "spk_a", Target = TrainingTarget.Singing, AutoCorrectionEnabled = false }],
            TestProject.Token);
        var job = Assert.Single(batch.Jobs);
        var preflight = new TrainingPreflightService(
            new TalkTrainingStrategyPlanner(BuiltInTalkStrategies.CreateRegistry(), new TexturePresetRegistry()),
            new SingingTrainingStrategyPlanner(BuiltInSingingStrategies.CreateRegistry(), new TexturePresetRegistry()), project.Jobs);
        var worker = new MlWorkerClient("worker-that-must-not-start", []);
        var pipeline = new SingingTrainingPipeline(
            new SpeakerInventoryProvider(project.Correction(), project.Classifications),
            new TrainingExecutionService(preflight, new TrainingJobRunner(project.Planner()), project.Jobs),
            new DiffSingerDatasetBuilder(project.DatasetRepository), new DiffSingerAcousticInputService(worker),
            new DiffSingerBinarizeService(worker), new DiffSingerTrainerService(worker), project.Jobs,
            _ => Task.FromResult(new HardwareInfo(false, null)),
            _ => Task.FromResult<IReadOnlyList<ResolverCheckpoint>>([]));
        var outcome = await pipeline.RunAsync(project.Workspace, batch.BatchId, job.JobId, "trainer", "checkpoints", TestProject.Token);
        Assert.Null(outcome.State);
        Assert.NotEmpty(outcome.StopReasons);
        TestProject.AssertNoTrainingInputPublished(project.Workspace);
        Assert.Equal(TrainingJobState.Pending,
            (await project.Jobs.LoadBatchAsync(project.Workspace, batch.BatchId, TestProject.Token))!.Jobs.Single().State);
    }

    [Fact]
    public async Task TheVocoderModelIsRequiredAndItsExpectedLocationIsReported()
    {
        using var project = new TestProject();
        var error = Assert.Throws<InvalidOperationException>(() => SingingTrainingPipeline.RequireVocoder(project.Workspace.RootPath));
        Assert.Contains("model.ckpt", error.Message);
        var model = Path.Combine(project.Workspace.RootPath, SingingTrainingPipeline.VocoderRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        await File.WriteAllBytesAsync(model, [1], TestProject.Token);
        SingingTrainingPipeline.RequireVocoder(project.Workspace.RootPath);
    }

    [Fact]
    public async Task BinarizeRefusesToRunWithoutAcousticInput()
    {
        using var project = new TestProject();
        var job = new TrainingJobRecord
        {
            JobId = "j",
            SpeakerId = "spk_a",
            Target = TrainingTarget.Singing,
            DatasetFingerprint = "fp",
            SegmentIds = ["s"],
            Backend = TrainingBackendCatalog.SingingBackend,
            BackendRevision = TrainingBackendCatalog.SingingRevision
        };
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new DiffSingerBinarizeService(new MlWorkerClient("worker-that-must-not-start", []))
                .BinarizeAsync(project.Workspace, job, "trainer", TestProject.Token));
    }
}
