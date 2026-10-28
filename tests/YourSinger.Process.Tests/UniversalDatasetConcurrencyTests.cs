using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Tests;

// { 責務: [UniversalDatasetConcurrencyTests: 古い読込結果から新しい編集を上書きしないことを確認する] }
public sealed class UniversalDatasetConcurrencyTests
{
    // { 責務: [StaleSnapshotCannotOverwriteTranscript: 別の編集後に古い保存を拒否する] }
    [Fact]
    public async Task StaleSnapshotCannotOverwriteTranscript()
    {
        using var project = new TestProject();
        var segment = project.Segment("concurrent", type: SegmentContentType.Singing);
        await project.SeedAsync(segment);
        var stale = await project.DatasetRepository.LoadSnapshotAsync(project.Workspace, TestProject.Token);
        await new UniversalFeatureOverrideService(project.DatasetRepository)
            .SetTranscriptAsync(project.Workspace, segment.SegmentId, "新しい文字起こし", TestProject.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => project.DatasetRepository.SaveIfUnchangedAsync(
            project.Workspace, stale.Record!, stale.Revision, TestProject.Token));
        var current = await project.DatasetRepository.LoadAsync(project.Workspace, TestProject.Token);
        var saved = Assert.Single(current!.Segments);
        Assert.Equal("新しい文字起こし", saved.Transcript);
        Assert.Null(saved.AlignmentMeasurement);
        Assert.Empty(saved.Phonemes);
    }

    // { 責務: [MissingSnapshotCannotOverwriteNewDataset: 不在の読込後に作成されたdatasetを保護する] }
    [Fact]
    public async Task MissingSnapshotCannotOverwriteNewDataset()
    {
        using var project = new TestProject();
        var stale = await project.DatasetRepository.LoadSnapshotAsync(project.Workspace, TestProject.Token);
        Assert.Null(stale.Record);
        await project.DatasetRepository.SaveAsync(project.Workspace, new UniversalVoiceDatasetRecord { SchemaVersion = "1", StageVersion = "test" }, TestProject.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => project.DatasetRepository.SaveIfUnchangedAsync(
            project.Workspace, new UniversalVoiceDatasetRecord { SchemaVersion = "1", StageVersion = "test" }, stale.Revision, TestProject.Token));
    }
    // { 責務: [ConcurrentSavesCannotBothReplaceSnapshot: 同じ版からの同時保存は一方だけ成功する] }
    [Fact]
    public async Task ConcurrentSavesCannotBothReplaceSnapshot()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("concurrent"));
        var first = await project.DatasetRepository.LoadSnapshotAsync(project.Workspace, TestProject.Token);
        var second = await project.DatasetRepository.LoadSnapshotAsync(project.Workspace, TestProject.Token);
        first.Record!.Segments[0].Transcript = "first";
        second.Record!.Segments[0].Transcript = "second";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Save(UniversalVoiceDatasetRecord record, string? revision)
        {
            await start.Task;
            try
            {
                await project.DatasetRepository.SaveIfUnchangedAsync(project.Workspace, record, revision, TestProject.Token);
                return true;
            }
            catch (IOException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        var firstSave = Save(first.Record, first.Revision);
        var secondSave = Save(second.Record, second.Revision);
        start.SetResult();
        var results = await Task.WhenAll(firstSave, secondSave);
        Assert.Single(results, result => result);
        var saved = await project.DatasetRepository.LoadAsync(project.Workspace, TestProject.Token);
        Assert.Contains(saved!.Segments[0].Transcript, new[] { "first", "second" });
    }

    // { 責務: [HeldLockWaitsAndCanCancel: 排他中は待機し取消後も既存内容を保護する] }
    [Fact]
    public async Task HeldLockWaitsAndCanCancel()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("locked"));
        var snapshot = await project.DatasetRepository.LoadSnapshotAsync(project.Workspace, TestProject.Token);
        var path = Path.Combine(project.Workspace.MetadataPath, "universal-voice-dataset.json.lock");
        using (var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var readCancellation = new CancellationTokenSource();
            var read = project.DatasetRepository.LoadSnapshotAsync(project.Workspace, readCancellation.Token);
            Assert.False(read.IsCompleted);
            readCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            using var saveCancellation = new CancellationTokenSource();
            var save = project.DatasetRepository.SaveIfUnchangedAsync(
                project.Workspace, snapshot.Record!, snapshot.Revision, saveCancellation.Token);
            Assert.False(save.IsCompleted);
            saveCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        }
        var saved = await project.DatasetRepository.LoadSnapshotAsync(project.Workspace, TestProject.Token);
        Assert.Equal(snapshot.Revision, saved.Revision);
    }

    // { 責務: [ReadResumesAfterLockRelease: 保存ロックが解放されれば読み込みを続ける] }
    [Fact]
    public async Task ReadResumesAfterLockRelease()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("waiting"));
        var path = Path.Combine(project.Workspace.MetadataPath, "universal-voice-dataset.json.lock");
        Task<UniversalVoiceDatasetRecord?> read;
        using (var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            read = project.DatasetRepository.LoadAsync(project.Workspace, TestProject.Token);
            Assert.False(read.IsCompleted);
        }
        var saved = await read;
        Assert.NotNull(saved);
    }

    // { 責務: [CanceledSavePreservesDataset: 取消された保存は既存データを変更しない] }
    [Fact]
    public async Task CanceledSavePreservesDataset()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("cancelled"));
        var snapshot = await project.DatasetRepository.LoadSnapshotAsync(project.Workspace, TestProject.Token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => project.DatasetRepository.SaveIfUnchangedAsync(
            project.Workspace, snapshot.Record!, snapshot.Revision, cancellation.Token));
        var saved = await project.DatasetRepository.LoadSnapshotAsync(project.Workspace, TestProject.Token);
        Assert.Equal(snapshot.Revision, saved.Revision);
    }

    // { 責務: [CanceledMissingLoadIsCanceled: 不在時も開始前の取消を優先する] }
    [Fact]
    public async Task CanceledMissingLoadIsCanceled()
    {
        using var project = new TestProject();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            project.DatasetRepository.LoadSnapshotAsync(project.Workspace, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            project.DatasetRepository.LoadAsync(project.Workspace, cancellation.Token));
    }

    // { 責務: [MissingDatasetStillHonorsLock: 未作成のdatasetも保存中は不在と誤認しない] }
    [Fact]
    public async Task MissingDatasetStillHonorsLock()
    {
        using var project = new TestProject();
        var path = Path.Combine(project.Workspace.MetadataPath, "universal-voice-dataset.json.lock");
        using var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        var read = project.DatasetRepository.LoadSnapshotAsync(project.Workspace, cancellation.Token);
        Assert.False(read.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }
}
