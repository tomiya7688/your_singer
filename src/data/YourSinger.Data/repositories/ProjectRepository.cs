using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Data.Repositories;

public sealed class ProjectRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task<ProjectManifest?> LoadAsync(ProjectWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(workspace.ManifestPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(workspace.ManifestPath);
        return await JsonSerializer.DeserializeAsync<ProjectManifest>(stream, JsonOptions, cancellationToken);
    }

    public async Task SaveAsync(ProjectWorkspace workspace, ProjectManifest manifest, CancellationToken cancellationToken = default)
    {
        workspace.EnsureCreated();
        manifest.UpdatedAt = DateTimeOffset.UtcNow;
        var temporaryPath = workspace.ManifestPath + ".tmp";

        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, workspace.ManifestPath, overwrite: true);
    }
}
