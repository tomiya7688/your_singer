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
}
