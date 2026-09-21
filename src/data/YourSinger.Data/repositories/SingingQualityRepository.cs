using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Data.Repositories;

public sealed class SingingQualityRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task<SingingQualityDiagnostic?> LoadAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(workspace.MetadataPath, "singing-quality.json");
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SingingQualityDiagnostic>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    public async Task SaveAsync(
        ProjectWorkspace workspace,
        SingingQualityDiagnostic diagnostic,
        CancellationToken cancellationToken = default)
    {
        workspace.EnsureCreated();
        var path = Path.Combine(workspace.MetadataPath, "singing-quality.json");
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
}
