using YourSinger.Data.Models;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Speaker;
using YourSinger.Process.Processing.Talk;

namespace YourSinger.Process.Tests;

public sealed class TrainingGuardrailTests
{
    private static CancellationToken Token => TestProject.Token;
    private static SpeakerTrainingSelection Selection(string speaker = "spk_a", TrainingTarget target = TrainingTarget.Talk,
        bool correction = false) => new() { SpeakerId = speaker, Target = target, AutoCorrectionEnabled = correction };

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-1)]
    public async Task InvalidTargetDoesNotPublishBatch(int target)
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => p.Planner().CreateBatchAsync(p.Workspace,
            [Selection(target: (TrainingTarget)target)], Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.MetadataPath, "training-jobs")));
    }

    [Fact]
    public async Task EmptySelectionIsRejected()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => p.Planner().CreateBatchAsync(p.Workspace, [], Token));
    }

    [Fact]
    public async Task DuplicateSpeakerSelectionIsRejected()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => p.Planner().CreateBatchAsync(p.Workspace,
            [Selection(), Selection()], Token));
    }

    [Fact]
    public async Task MissingSpeakerIsRejected()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.Planner().CreateBatchAsync(p.Workspace,
            [Selection("not-found")], Token));
    }

    [Fact]
    public async Task MissingTargetDataDoesNotPublishPartialBatch()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.Planner().CreateBatchAsync(p.Workspace,
            [Selection(target: TrainingTarget.Both)], Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.MetadataPath, "training-jobs")));
    }

    [Fact]
    public async Task PendingJobCannotSkipToSuccess()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var batch = await p.Planner().CreateBatchAsync(p.Workspace, [Selection()], Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.Planner().UpdateStateAsync(p.Workspace,
            batch.BatchId, batch.Jobs[0].JobId, TrainingJobState.Succeeded, cancellationToken: Token));
        Assert.Equal(TrainingJobState.Pending, (await p.Jobs.LoadBatchAsync(p.Workspace, batch.BatchId, Token))!.Jobs[0].State);
    }

    [Fact]
    public async Task TerminalJobCannotRestart()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var batch = await p.Planner().CreateBatchAsync(p.Workspace, [Selection()], Token);
        var id = batch.Jobs[0].JobId;
        await p.Planner().UpdateStateAsync(p.Workspace, batch.BatchId, id, TrainingJobState.Running, cancellationToken: Token);
        await p.Planner().UpdateStateAsync(p.Workspace, batch.BatchId, id, TrainingJobState.Failed, errorMessage: "検証用エラー", cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.Planner().UpdateStateAsync(p.Workspace,
            batch.BatchId, id, TrainingJobState.Running, cancellationToken: Token));
        var saved = (await p.Jobs.LoadBatchAsync(p.Workspace, batch.BatchId, Token))!.Jobs[0];
        Assert.Equal(TrainingJobState.Failed, saved.State);
        Assert.NotNull(saved.StartedAt); Assert.NotNull(saved.CompletedAt);
    }

    [Theory]
    [InlineData(TrainingTarget.Talk)]
    [InlineData(TrainingTarget.Singing)]
    public async Task EditedInputInvalidatesAlreadyPlannedJob(TrainingTarget target)
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a", type: target == TrainingTarget.Talk ? SegmentContentType.Speech : SegmentContentType.Singing));
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Selection(target: target)], Token)).Jobs);
        var data = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        data.Segments[0].Transcript = "計画後の編集";
        await p.DatasetRepository.SaveAsync(p.Workspace, data, Token);
        if (target == TrainingTarget.Talk)
            await Assert.ThrowsAsync<InvalidOperationException>(() => new StyleBertVits2DatasetBuilder(p.DatasetRepository)
                .BuildAsync(p.Workspace, job, "話者", Token));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => new DiffSingerDatasetBuilder(p.DatasetRepository)
                .BuildAsync(p.Workspace, job, Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.RootPath, "training")));
    }

    [Fact]
    public async Task CanceledPlanningDoesNotPublishBatch()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.Planner().CreateBatchAsync(p.Workspace,
            [Selection()], canceled.Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.MetadataPath, "training-jobs")));
    }

    [Fact]
    public async Task MissingAudioDoesNotLeavePublishedOrStagingInput()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"), p.Segment("b"));
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Selection()], Token)).Jobs);
        var original = await File.ReadAllBytesAsync(Path.Combine(p.Workspace.SegmentsPath, "a.wav"), Token);
        File.Delete(Path.Combine(p.Workspace.SegmentsPath, "b.wav"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => new StyleBertVits2DatasetBuilder(p.DatasetRepository)
            .BuildAsync(p.Workspace, job, "話者", Token));
        var parent = Path.Combine(p.Workspace.RootPath, "training", "style-bert-vits2");
        Assert.False(Directory.Exists(Path.Combine(parent, job.JobId)));
        Assert.Empty(Directory.GetDirectories(parent, ".input-*.tmp"));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(p.Workspace.SegmentsPath, "a.wav"), Token));
    }

    [Fact]
    public async Task CompletedInputIsNeverOverwrittenByRetry()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Selection()], Token)).Jobs);
        var builder = new StyleBertVits2DatasetBuilder(p.DatasetRepository);
        var first = await builder.BuildAsync(p.Workspace, job, "話者", Token);
        var before = await File.ReadAllBytesAsync(Assert.Single(first.Items).AudioPath, Token);
        await Assert.ThrowsAsync<IOException>(() => builder.BuildAsync(p.Workspace, job, "別名", Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(first.Items[0].AudioPath, Token));
    }

    [Fact]
    public async Task MixedCorrectionSettingsSurviveReplanning()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"), p.Segment("b", "spk_b"));
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [Selection(), Selection("spk_b", correction: true)], Token);
        var retry = await p.Planner().RecreateBatchAsync(p.Workspace, batch, Token);
        Assert.False(Assert.Single(retry.Jobs, x => x.SpeakerId == "spk_a").AutoCorrectionEnabled);
        Assert.True(Assert.Single(retry.Jobs, x => x.SpeakerId == "spk_b").AutoCorrectionEnabled);
    }

    [Fact]
    public async Task UnknownExclusionDoesNotOverwriteAnalysis()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var path = Path.Combine(p.Workspace.MetadataPath, "speakers.json");
        var before = await File.ReadAllBytesAsync(path, Token);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new SpeakerOverrideService(p.Speakers)
            .ExcludeSegmentAsync(p.Workspace, "missing", true, Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
    }
}
