using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Data.Repositories;

public sealed class SpeakerAnalysisRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<SpeakerAnalysisRecord?> LoadAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(workspace);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SpeakerAnalysisRecord>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    public async Task SaveAsync(
        ProjectWorkspace workspace,
        SpeakerAnalysisRecord record,
        CancellationToken cancellationToken = default)
    {
        workspace.EnsureCreated();
        var path = GetPath(workspace);
        var temporaryPath = path + ".tmp";

        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string GetPath(ProjectWorkspace workspace) =>
        Path.Combine(workspace.MetadataPath, "speakers.json");
}
