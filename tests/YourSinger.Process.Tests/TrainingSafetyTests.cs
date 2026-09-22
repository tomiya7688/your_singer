using YourSinger.Data.Models;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Talk;

namespace YourSinger.Process.Tests;

public sealed class TrainingSafetyTests
{
    private static CancellationToken Token => TestProject.Token;

    [Fact]
    public async Task MissingTargetDataDoesNotCreateEmptyJob()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Singing }], Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.MetadataPath, "training-jobs")));
    }

    [Fact]
    public async Task InvalidTargetDoesNotCreateBatch()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        await Assert.ThrowsAsync<ArgumentException>(() => p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.None }], Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.MetadataPath, "training-jobs")));
    }

    [Fact]
    public async Task ChangedInputRequiresNewJobBeforeEitherBuilderRuns()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("talk"), p.Segment("song", type: SegmentContentType.Singing));
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Both }], Token);
        var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        dataset.Segments[0].Transcript = "ジョブ作成後に変更した文章";
        await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StyleBertVits2DatasetBuilder(p.DatasetRepository)
            .BuildAsync(p.Workspace, Assert.Single(batch.Jobs, x => x.Target == TrainingTarget.Talk), "話者", Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DiffSingerDatasetBuilder(p.DatasetRepository)
            .BuildAsync(p.Workspace, Assert.Single(batch.Jobs, x => x.Target == TrainingTarget.Singing), Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.RootPath, "training")));
    }

    [Fact]
    public async Task RebuildingDatasetPreservesExistingTrainingLog()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk }], Token)).Jobs);
        var builder = new StyleBertVits2DatasetBuilder(p.DatasetRepository);
        await builder.BuildAsync(p.Workspace, job, "話者", Token);
        var trainer = Path.Combine(p.Workspace.RootPath, "training", "style-bert-vits2", job.JobId, "trainer");
        Directory.CreateDirectory(trainer);
        var log = Path.Combine(trainer, "trainer.log");
        await File.WriteAllTextAsync(log, "既存学習結果", Token);
        await builder.BuildAsync(p.Workspace, job, "話者", Token);
        Assert.Equal("既存学習結果", await File.ReadAllTextAsync(log, Token));
    }

    [Fact]
    public async Task MissingAudioDoesNotDestroyPublishedDataset()
    {
        using var p = new TestProject();
        var segment = p.Segment("a");
        await p.SeedAsync(segment);
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk }], Token)).Jobs);
        var builder = new StyleBertVits2DatasetBuilder(p.DatasetRepository);
        var first = await builder.BuildAsync(p.Workspace, job, "話者", Token);
        var outputPath = Assert.Single(first.Items).AudioPath;
        var before = await File.ReadAllBytesAsync(outputPath, Token);
        File.Delete(Path.Combine(p.Workspace.RootPath, segment.AudioPath));
        await Assert.ThrowsAsync<FileNotFoundException>(() => builder.BuildAsync(p.Workspace, job, "話者", Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(outputPath, Token));
    }

    [Fact]
    public async Task EsdListHasFourColumnsAndWorkspaceRelativeAudioIsCopied()
    {
        using var p = new TestProject();
        var segment = p.Segment("a");
        segment.Transcript = "あ|い\nう";
        await p.SeedAsync(segment);
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk }], Token)).Jobs);
        var result = await new StyleBertVits2DatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, "話者", Token);
        var lines = await File.ReadAllLinesAsync(Path.Combine(p.Workspace.RootPath,
            "training", "style-bert-vits2", job.JobId, "dataset", "esd.list"), Token);
        var fields = Assert.Single(lines).Split('|');
        Assert.Equal(new[] { "raw/segment_000000.wav", "話者", "JP", "あ い う" }, fields);
        Assert.True(File.Exists(Assert.Single(result.Items).AudioPath));
    }
}
