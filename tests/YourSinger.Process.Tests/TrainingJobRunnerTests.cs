using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// {
//   責務: [TrainingJobRunnerTests: 成功・失敗・取消が別々の終了状態として保存され、再実行できないことを確認する]
// }
public sealed class TrainingJobRunnerTests
{
    private static readonly SpeakerTrainingSelection Singing = new()
    {
        SpeakerId = "spk_a", Target = TrainingTarget.Singing, AutoCorrectionEnabled = false
    };

    private static async Task<(TestProject Project, TrainingJobPlanningService Planner, TrainingJobBatch Batch)> ArrangeAsync()
    {
        var project = new TestProject();
        await project.SeedAsync(project.Segment("a", type: SegmentContentType.Singing));
        var planner = project.Planner();
        var batch = await planner.CreateBatchAsync(project.Workspace, [Singing], TestProject.Token);
        return (project, planner, batch);
    }

    private static async Task<TrainingJobRecord> ReloadAsync(TestProject project, TrainingJobPlanningService planner, TrainingJobBatch batch)
    {
        var loaded = await project.Jobs.LoadBatchAsync(project.Workspace, batch.BatchId, TestProject.Token);
        return Assert.Single(loaded!.Jobs);
    }

    [Fact]
    public async Task SuccessFailureAndCancellationAreRecordedAsDistinctStates()
    {
        var (project, planner, batch) = await ArrangeAsync();
        using (project)
        {
            var runner = new TrainingJobRunner(planner);
            var jobId = Assert.Single(batch.Jobs).JobId;
            Assert.Equal(TrainingJobState.Failed, await runner.RunAsync(project.Workspace, batch.BatchId, jobId,
                _ => throw new InvalidOperationException("終了コード7"), TestProject.Token));
            var failed = await ReloadAsync(project, planner, batch);
            Assert.Equal(TrainingJobState.Failed, failed.State);
            Assert.Contains("終了コード7", failed.ErrorMessage);
            Assert.NotNull(failed.CompletedAt);

            // 終了済みのjobは再実行できない(再学習は新しいjobで行う)。
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(project.Workspace, batch.BatchId, jobId,
                _ => Task.FromResult("log"), TestProject.Token));
        }
    }

    [Fact]
    public async Task CancellationIsRecordedEvenThoughTheTokenIsAlreadyCanceled()
    {
        var (project, planner, batch) = await ArrangeAsync();
        using (project)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestProject.Token);
            var jobId = Assert.Single(batch.Jobs).JobId;
            var state = await new TrainingJobRunner(planner).RunAsync(project.Workspace, batch.BatchId, jobId, async token =>
            {
                await cancel.CancelAsync();
                token.ThrowIfCancellationRequested();
                return "log";
            }, cancel.Token);
            Assert.Equal(TrainingJobState.Canceled, state);
            Assert.Equal(TrainingJobState.Canceled, (await ReloadAsync(project, planner, batch)).State);
        }
    }

    [Fact]
    public async Task ASuccessfulRunIsRecordedAsSucceeded()
    {
        var (project, planner, batch) = await ArrangeAsync();
        using (project)
        {
            var jobId = Assert.Single(batch.Jobs).JobId;
            Assert.Equal(TrainingJobState.Succeeded, await new TrainingJobRunner(planner).RunAsync(
                project.Workspace, batch.BatchId, jobId, _ => Task.FromResult("log"), TestProject.Token));
            var job = await ReloadAsync(project, planner, batch);
            Assert.Equal((TrainingJobState.Succeeded, null), (job.State, job.ErrorMessage));
        }
    }
}
