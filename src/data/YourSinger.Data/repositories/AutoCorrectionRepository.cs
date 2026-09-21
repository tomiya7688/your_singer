using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Data.Repositories;

public sealed class AutoCorrectionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task<(AutoCorrectionSettings Settings, List<CorrectionRecord> Corrections)> LoadAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(workspace.MetadataPath, "auto-correction.json");
        if (!File.Exists(path))
            return (new AutoCorrectionSettings(), []);

        await using var stream = File.OpenRead(path);
        var payload = await JsonSerializer.DeserializeAsync<AutoCorrectionPayload>(
            stream, JsonOptions, cancellationToken);

        return payload is null
            ? (new AutoCorrectionSettings(), [])
            : (payload.Settings, payload.Corrections);
    }

    public async Task SaveAsync(
        ProjectWorkspace workspace,
        AutoCorrectionSettings settings,
        IReadOnlyList<CorrectionRecord> corrections,
        CancellationToken cancellationToken = default)
    {
        workspace.EnsureCreated();
        var path = Path.Combine(workspace.MetadataPath, "auto-correction.json");
        var temp = path + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                new AutoCorrectionPayload
                {
                    Settings = settings,
                    Corrections = [.. corrections]
                },
                JsonOptions,
                cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    private sealed class AutoCorrectionPayload
    {
        public AutoCorrectionSettings Settings { get; init; } = new();
        public List<CorrectionRecord> Corrections { get; init; } = [];
    }
}
