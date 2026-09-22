using System.Text.Json.Nodes;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Speaker;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

public sealed class TrainingRecreationTests
{
    private static CancellationToken Token => TestProject.Token;
    private static SpeakerTrainingSelection Select(string speaker = "spk_a",
        TrainingTarget target = TrainingTarget.Talk, bool correction = false) => new()
    {
        SpeakerId = speaker, Target = target, AutoCorrectionEnabled = correction
    };
    private static string BatchPath(TestProject p, string id) =>
        Path.Combine(p.Workspace.MetadataPath, "training-jobs", id + ".json");

    private static async Task<TrainingJobBatch> RemoveVersionAsync(TestProject p, TrainingJobBatch batch)
    {
        var path = BatchPath(p, batch.BatchId);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
        node.Remove("input_version");
        node.Remove("recreated_from_batch_id");
        foreach (var job in node["jobs"]!.AsArray()) job!["dataset_fingerprint"] = "legacy-fingerprint";
        await File.WriteAllTextAsync(path, node.ToJsonString(), Token);
        return (await p.Jobs.LoadBatchAsync(p.Workspace, batch.BatchId, Token))!;
    }

    [Fact]
    public async Task MissingVersionInOldJsonIsReadableAndRequiresRecreation()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var current = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        Assert.False(TrainingJobPlanningService.RequiresRecreation(current));
        var old = await RemoveVersionAsync(p, current);
        Assert.Equal(string.Empty, old.InputVersion);
        Assert.Null(old.RecreatedFromBatchId);
        Assert.True(TrainingJobPlanningService.RequiresRecreation(old));
    }

    [Fact]
    public async Task RecreationPreservesOriginalRecordsModelsAndPerSpeakerSettings()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"), p.Segment("b", "spk_b"),
            p.Segment("song", "spk_b", SegmentContentType.Singing));
        var source = await p.Planner().CreateBatchAsync(p.Workspace,
            [Select(), Select("spk_b", TrainingTarget.Both, true)], Token);
        var artifactDirectory = Path.Combine(p.Workspace.RootPath, "training", source.Jobs[0].JobId);
        Directory.CreateDirectory(artifactDirectory);
        var model = Path.Combine(artifactDirectory, "weights.fixture");
        var log = Path.Combine(artifactDirectory, "trainer.log");
        await File.WriteAllTextAsync(model, "既存の成果物を模した保護対象", Token);
        await File.WriteAllTextAsync(log, "既存の学習ログ", Token);
        foreach (var job in source.Jobs)
        {
            job.State = TrainingJobState.Failed;
            job.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.ErrorMessage = "以前の失敗";
            job.Artifacts.Add(new() { Kind = "test-only", Path = model });
        }
        await p.Jobs.SaveBatchAsync(p.Workspace, source, Token);
        source = await RemoveVersionAsync(p, source);
        var original = await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token);
        var oldModel = await File.ReadAllBytesAsync(model, Token);
        var oldLog = await File.ReadAllBytesAsync(log, Token);

        var recreated = await p.Planner().RecreateBatchAsync(p.Workspace, source.BatchId, Token);
        Assert.NotEqual(source.BatchId, recreated.BatchId);
        Assert.Equal(source.BatchId, recreated.RecreatedFromBatchId);
        Assert.Equal(TrainingJobPlanningService.CurrentInputVersion, recreated.InputVersion);
        Assert.Equal(source.Jobs.Select(x => (x.SpeakerId, x.Target, x.AutoCorrectionEnabled)),
            recreated.Jobs.Select(x => (x.SpeakerId, x.Target, x.AutoCorrectionEnabled)));
        Assert.All(recreated.Jobs, job =>
        {
            Assert.DoesNotContain(source.Jobs, old => old.JobId == job.JobId);
            Assert.Equal(TrainingJobState.Pending, job.State);
            Assert.Null(job.StartedAt); Assert.Null(job.CompletedAt); Assert.Null(job.ErrorMessage);
            Assert.Empty(job.Artifacts);
            Assert.NotEqual("legacy-fingerprint", job.DatasetFingerprint);
        });
        Assert.Equal(original, await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token));
        Assert.Equal(oldModel, await File.ReadAllBytesAsync(model, Token));
        Assert.Equal(oldLog, await File.ReadAllBytesAsync(log, Token));
        var saved = (await p.Jobs.LoadBatchAsync(p.Workspace, recreated.BatchId, Token))!;
        Assert.Equal(source.BatchId, saved.RecreatedFromBatchId);
        Assert.Equal(recreated.InputVersion, saved.InputVersion);
    }

    [Fact]
    public async Task RecreationAppliesCurrentManualExclusionWithoutRewritingSourceBatch()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("keep"), p.Segment("exclude"));
        var source = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        var original = await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token);
        await new SpeakerOverrideService(p.Speakers).ExcludeSegmentAsync(p.Workspace, "exclude", true, Token);
        var recreated = await p.Planner().RecreateBatchAsync(p.Workspace, source, Token);
        Assert.Equal(new[] { "keep" }, Assert.Single(recreated.Jobs).SegmentIds);
        Assert.Equal(original, await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token));
    }

    [Fact]
    public async Task RecreationUsesEditedTranscriptForNewFingerprint()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var source = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        dataset.Segments[0].Transcript = "再作成前に修正した文章";
        await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
        var recreated = await p.Planner().RecreateBatchAsync(p.Workspace, source, Token);
        Assert.NotEqual(source.Jobs[0].DatasetFingerprint, recreated.Jobs[0].DatasetFingerprint);
    }

    [Fact]
    public async Task RunningStateIsReloadedInsteadOfTrustingDisplayedSnapshot()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var displayed = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        await p.Planner().UpdateStateAsync(p.Workspace, displayed.BatchId, displayed.Jobs[0].JobId,
            TrainingJobState.Running, cancellationToken: Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            p.Planner().RecreateBatchAsync(p.Workspace, displayed, Token));
        Assert.Single((await p.Jobs.ListBatchesAsync(p.Workspace, Token)).Batches);
        Assert.Equal(TrainingJobState.Running,
            (await p.Jobs.LoadBatchAsync(p.Workspace, displayed.BatchId, Token))!.Jobs[0].State);
    }

    [Fact]
    public async Task RemovedSourceIsNotRecreatedFromStaleInMemoryCopy()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var source = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        File.Delete(BatchPath(p, source.BatchId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            p.Planner().RecreateBatchAsync(p.Workspace, source, Token));
        Assert.Empty((await p.Jobs.ListBatchesAsync(p.Workspace, Token)).Batches);
    }

    [Fact]
    public async Task MissingTargetAfterEditsDoesNotPublishPartialRecreation()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("talk"), p.Segment("song", type: SegmentContentType.Singing));
        var source = await p.Planner().CreateBatchAsync(p.Workspace, [Select(target: TrainingTarget.Both)], Token);
        var original = await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token);
        await new SpeakerOverrideService(p.Speakers).ExcludeSegmentAsync(p.Workspace, "song", true, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            p.Planner().RecreateBatchAsync(p.Workspace, source, Token));
        Assert.Single((await p.Jobs.ListBatchesAsync(p.Workspace, Token)).Batches);
        Assert.Equal(original, await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token));
    }

    [Fact]
    public async Task CanceledRecreationPreservesHistory()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var source = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        var original = await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            p.Planner().RecreateBatchAsync(p.Workspace, source, cancellation.Token));
        Assert.Single((await p.Jobs.ListBatchesAsync(p.Workspace, Token)).Batches);
        Assert.Equal(original, await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token));
    }

    [Fact]
    public async Task NewBatchPublicationNeverOverwritesExistingId()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var source = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        var original = await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token);
        source.Jobs[0].ErrorMessage = "上書きしない";
        await Assert.ThrowsAsync<IOException>(() => p.Jobs.CreateBatchAsync(p.Workspace, source, Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(BatchPath(p, source.BatchId))!, "*.tmp"));
    }

    [Fact]
    public async Task ListingIsReadOnlyAndIsolatesBrokenAndMismatchedRecords()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var source = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        var original = await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token);
        await File.WriteAllTextAsync(BatchPath(p, "broken"), "{", Token);
        await File.WriteAllBytesAsync(BatchPath(p, "mismatched-id"), original, Token);
        var correctionPath = Path.Combine(p.Workspace.MetadataPath, "auto-correction.json");
        var correctionBefore = await File.ReadAllBytesAsync(correctionPath, Token);
        var listed = await p.Jobs.ListBatchesAsync(p.Workspace, Token);
        Assert.Equal(source.BatchId, Assert.Single(listed.Batches).BatchId);
        Assert.Equal(2, listed.Errors.Count);
        Assert.Equal(original, await File.ReadAllBytesAsync(BatchPath(p, source.BatchId), Token));
        Assert.Equal(correctionBefore, await File.ReadAllBytesAsync(correctionPath, Token));
        Assert.Equal("{", await File.ReadAllTextAsync(BatchPath(p, "broken"), Token));
    }

    [Fact]
    public async Task EmptyHistoryDoesNotCreateDirectories()
    {
        using var p = new TestProject();
        var listed = await p.Jobs.ListBatchesAsync(p.Workspace, Token);
        Assert.Empty(listed.Batches); Assert.Empty(listed.Errors);
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.MetadataPath, "training-jobs")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("bad/name")]
    [InlineData("bad\\name")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("C:batch")]
    public async Task BatchIdCannotEscapeHistoryDirectory(string id)
    {
        using var p = new TestProject();
        await Assert.ThrowsAsync<InvalidDataException>(() => p.Jobs.LoadBatchAsync(p.Workspace, id, Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.MetadataPath, "training-jobs")));
    }

    [Fact]
    public async Task ExplicitRepeatedRecreationHasDistinctIdsAndKeepsLineage()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var source = await p.Planner().CreateBatchAsync(p.Workspace, [Select()], Token);
        var first = await p.Planner().RecreateBatchAsync(p.Workspace, source, Token);
        var second = await p.Planner().RecreateBatchAsync(p.Workspace, source, Token);
        Assert.NotEqual(first.BatchId, second.BatchId);
        Assert.NotEqual(first.Jobs[0].JobId, second.Jobs[0].JobId);
        Assert.Equal(source.BatchId, first.RecreatedFromBatchId);
        Assert.Equal(source.BatchId, second.RecreatedFromBatchId);
        Assert.Equal(3, (await p.Jobs.ListBatchesAsync(p.Workspace, Token)).Batches.Count);
    }
}
