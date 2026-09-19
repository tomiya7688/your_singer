using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Data.Repositories;

public sealed class PhonemeCoverageRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task SaveAsync(
        ProjectWorkspace workspace,
        PhonemeCoverageDiagnostic diagnostic,
        CancellationToken cancellationToken = default)
    {
        workspace.EnsureCreated();
        var path = GetPath(workspace);
        var temporaryPath = path + ".tmp";

        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                diagnostic,
                JsonOptions,
                cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public async Task<PhonemeCoverageDiagnostic?> LoadAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(workspace);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PhonemeCoverageDiagnostic>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    private static string GetPath(ProjectWorkspace workspace) =>
        Path.Combine(workspace.MetadataPath, "phoneme-coverage.json");
}
