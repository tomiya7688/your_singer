using YourSinger.Data.Models;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// ジョブ作成時に評価を保存し、学習入力fingerprintを記録する結線を確認する。
public sealed class TrainingInputWiringTests
{
    private static CancellationToken Token => TestProject.Token;

    private static SpeakerTrainingSelection Select(
        TrainingTarget target = TrainingTarget.Talk,
        TrainingJobOptions? talk = null,
        bool correction = false) => new()
    {
        SpeakerId = "spk_a", Target = target, AutoCorrectionEnabled = correction, TalkOptions = talk
    };

    private static async Task<TestProject> SeedAsync()
    {
        var p = new TestProject();
        await p.SeedAsync(p.Segment("a"), p.Segment("b"), p.Segment("song", type: SegmentContentType.Singing));
        return p;
    }

    [Fact]
    public async Task JobsRecordFingerprintBackendAndSavedAssessment()
    {
        using var p = await SeedAsync();
        var batch = await p.Planner().CreateBatchAsync(p.Workspace, [Select(TrainingTarget.Both)], Token);
        var talk = batch.Jobs.Single(x => x.Target == TrainingTarget.Talk);
        var singing = batch.Jobs.Single(x => x.Target == TrainingTarget.Singing);
        Assert.Matches("^[0-9a-f]{64}$", talk.InputFingerprint!);
        Assert.Equal(TrainingBackendCatalog.TalkBackend, talk.Backend);
        Assert.Equal(TrainingBackendCatalog.SingingRevision, singing.BackendRevision);
        var assessment = await new TrainingAssessmentRepository().LoadAsync(p.Workspace, talk.JobId, Token);
        Assert.Equal(["a", "b"], assessment!.Items.Select(x => x.SegmentId));
        Assert.All(assessment.Items, x => Assert.True(x.TrainingWeight > 0));
        var loaded = (await p.Jobs.LoadBatchAsync(p.Workspace, batch.BatchId, Token))!;
        Assert.Equal(talk.InputFingerprint, loaded.Jobs.Single(x => x.Target == TrainingTarget.Talk).InputFingerprint);
    }

    [Fact]
    public async Task Fingerprint_Fixed_ForTheSameInputAcrossBatchesAndRecreation()
    {
        using var p = await SeedAsync();
        var first = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        var second = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        var recreated = await p.Planner().RecreateBatchAsync(p.Workspace, first.BatchId, Token);
        Assert.NotEqual(first.Jobs[0].JobId, second.Jobs[0].JobId);
        Assert.Equal(first.Jobs[0].InputFingerprint, second.Jobs[0].InputFingerprint);
        Assert.Equal(first.Jobs[0].InputFingerprint, recreated.Jobs[0].InputFingerprint);
    }

    [Fact]
    public async Task Fingerprint_ChangesOnModeAndTexture()
    {
        using var p = await SeedAsync();
        var baseline = (await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token)).Jobs[0].InputFingerprint;
        var scratch = (await p.Planner().CreateBatchAsync(p.Workspace,
            [Select(talk: new() { Mode = TrainingMode.FromScratch })], Token)).Jobs[0].InputFingerprint;
        var texture = (await p.Planner().CreateBatchAsync(p.Workspace,
            [Select(talk: new() { Texture = new TrainingTextureRequest { Preset = "bright" } })], Token)).Jobs[0].InputFingerprint;
        Assert.NotEqual(baseline, scratch);
        Assert.NotEqual(baseline, texture);
    }

    [Fact]
    public async Task NoEligibleSegments_FailsWithoutSavingAnything()
    {
        using var p = new TestProject();
        var silent = p.Segment("silent");
        silent.VoicedRatio = 0;
        await p.SeedAsync(silent);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token));
        Assert.Contains("利用可能な区間がありません", error.Message);
        Assert.Empty((await p.Jobs.ListBatchesAsync(p.Workspace, Token)).Batches);
        var root = Path.Combine(p.Workspace.TrainingPath, "assessments");
        Assert.True(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any());
    }

    [Fact]
    public async Task Canceled_NoPartialSave()
    {
        using var p = await SeedAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            p.Planner().CreateBatchAsync(p.Workspace, [Select(TrainingTarget.Both)], cts.Token));
        Assert.Empty((await p.Jobs.ListBatchesAsync(p.Workspace, Token)).Batches);
        var root = Path.Combine(p.Workspace.TrainingPath, "assessments");
        Assert.True(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any());
    }

    [Fact]
    public async Task SingingJobUsesF0RangeFromFeatureFile()
    {
        using var p = await SeedAsync();
        var batch = await p.Planner().CreateBatchAsync(p.Workspace, [Select(TrainingTarget.Singing)], Token);
        var item = (await new TrainingAssessmentRepository().LoadAsync(p.Workspace, batch.Jobs[0].JobId, Token))!.Items.Single();
        Assert.Equal(ConfidenceStatus.Measured, item.Utility.Status);
    }

    [Fact]
    public async Task OldVersionBatches_RequireRecreation()
    {
        using var p = await SeedAsync();
        var batch = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        Assert.False(TrainingJobPlanningService.RequiresRecreation(batch));
        var old = new TrainingJobBatch { BatchId = "old", InputVersion = "v1-09.5/" + "x", Jobs = batch.Jobs };
        Assert.True(TrainingJobPlanningService.RequiresRecreation(old));
    }
}
