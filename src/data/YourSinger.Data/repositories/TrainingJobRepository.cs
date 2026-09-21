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

    public async Task SaveBatchAsync(
        ProjectWorkspace workspace,
        TrainingJobBatch batch,
        CancellationToken cancellationToken = default)
    {
        workspace.EnsureCreated();
        var directory = Path.Combine(workspace.MetadataPath, "training-jobs");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, batch.BatchId + ".json");
        var temp = path + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, batch, JsonOptions, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    public async Task<TrainingJobBatch?> LoadBatchAsync(
        ProjectWorkspace workspace,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(
            workspace.MetadataPath,
            "training-jobs",
            batchId + ".json");

        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TrainingJobBatch>(
            stream,
            JsonOptions,
            cancellationToken);
    }
}
