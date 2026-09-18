namespace YourSinger.Data.Models;

public sealed class ProjectManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string ProjectId { get; init; }
    public required string Name { get; set; }
    public required string AnalysisVersion { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> InputRoots { get; init; } = [];
    public List<SourceRecord> Sources { get; init; } = [];
}
