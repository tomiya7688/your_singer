using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Data.Repositories;

public sealed class TrainingJobRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public Task SaveBatchAsync(ProjectWorkspace workspace, TrainingJobBatch batch,
        CancellationToken cancellationToken = default) =>
        WriteAsync(workspace, batch, overwrite: true, cancellationToken);

    public Task CreateBatchAsync(ProjectWorkspace workspace, TrainingJobBatch batch,
        CancellationToken cancellationToken = default) =>
        WriteAsync(workspace, batch, overwrite: false, cancellationToken);

    private static async Task WriteAsync(ProjectWorkspace workspace, TrainingJobBatch batch,
        bool overwrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBatch(batch, batch.BatchId);
        var path = GetPath(workspace, batch.BatchId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, batch, JsonOptions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<TrainingJobBatch?> LoadBatchAsync(ProjectWorkspace workspace, string batchId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(workspace, batchId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var batch = await JsonSerializer.DeserializeAsync<TrainingJobBatch>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("学習ジョブの保存データが空です。");
        ValidateBatch(batch, batchId);
        return batch;
    }

    /// <summary>履歴の閲覧では保存・補正処理を実行しない。</summary>
    public async Task<TrainingBatchHistory> ListBatchesAsync(ProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new TrainingBatchHistory();
        var directory = Path.Combine(workspace.MetadataPath, "training-jobs");
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var batch = await LoadBatchAsync(workspace, Path.GetFileNameWithoutExtension(path), cancellationToken);
                if (batch is not null) result.Batches.Add(batch);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                result.Errors.Add(new(Path.GetFileName(path),
                    "保存データを読み込めません。元ファイルは変更していません。"));
            }
        }
        result.Batches.Sort((a, b) =>
        {
            var time = b.CreatedAt.CompareTo(a.CreatedAt);
            return time != 0 ? time : StringComparer.Ordinal.Compare(a.BatchId, b.BatchId);
        });
        return result;
    }

    private static string GetPath(ProjectWorkspace workspace, string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId) || batchId is "." or ".." ||
            batchId.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) ||
            batchId.EndsWith('.') || batchId.EndsWith(' '))
            throw new InvalidDataException("学習ジョブの識別子が不正です。");
        return Path.Combine(workspace.MetadataPath, "training-jobs", batchId + ".json");
    }

    private static void ValidateBatch(TrainingJobBatch batch, string expectedId)
    {
        if (batch.BatchId != expectedId || batch.Jobs is null || batch.Jobs.Count == 0 ||
            batch.Jobs.Any(job => job is null || string.IsNullOrWhiteSpace(job.JobId) ||
                string.IsNullOrWhiteSpace(job.SpeakerId) || string.IsNullOrWhiteSpace(job.DatasetFingerprint) ||
                job.SegmentIds is null || job.Artifacts is null || !Enum.IsDefined(job.State) ||
                job.Target is not (TrainingTarget.Talk or TrainingTarget.Singing or TrainingTarget.Both)) ||
            batch.Jobs.Select(job => job.JobId).Distinct(StringComparer.Ordinal).Count() != batch.Jobs.Count)
            throw new InvalidDataException("学習ジョブの保存データが不正です。元ファイルを確認してください。");
    }
}
