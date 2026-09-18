namespace YourSinger.Data.Models;

public enum MediaType
{
    Audio,
    Video
}

public enum SourceAnalysisState
{
    Pending,
    Ready,
    Failed
}

public sealed class SourceRecord
{
    public required string SourceId { get; init; }
    public required string Path { get; set; }
    public required MediaType MediaType { get; set; }
    public long Size { get; set; }
    public DateTimeOffset ModifiedTime { get; set; }
    public required string ContentHash { get; set; }
    public required string AnalysisVersion { get; set; }
    public string? ExtractedAudioPath { get; set; }
    public SourceAnalysisState AnalysisState { get; set; } = SourceAnalysisState.Pending;
    public string? ErrorMessage { get; set; }
}
