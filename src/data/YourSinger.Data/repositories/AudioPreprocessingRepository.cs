using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Data.Repositories;

public sealed class AudioPreprocessingRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task SaveAsync(
        ProjectWorkspace workspace,
        AudioPreprocessingRecord record,
        CancellationToken cancellationToken = default)
    {
        workspace.EnsureCreated();
        var directory = Path.Combine(workspace.MetadataPath, "preprocessing");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{record.SourceId}.json");
        var temporaryPath = path + ".tmp";

        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public async Task<AudioPreprocessingRecord?> LoadAsync(
        ProjectWorkspace workspace,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(workspace.MetadataPath, "preprocessing", $"{sourceId}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AudioPreprocessingRecord>(
            stream,
            JsonOptions,
            cancellationToken);
    }
}
