namespace YourSinger.Data.Models;

public sealed class AudioPreprocessingRecord
{
    public required string SourceId { get; init; }
    public required string StageVersion { get; init; }
    public required string RawAudioPath { get; init; }
    public string? SeparatedAudioPath { get; set; }
    public string? CleanedAudioPath { get; set; }
    public List<AudioSegmentRecord> Segments { get; init; } = [];
    public AudioQualityMetrics Quality { get; set; } = new();
    public List<ProcessingStageRecord> History { get; init; } = [];
    public string? ErrorMessage { get; set; }
}

public sealed class AudioSegmentRecord
{
    public required string SegmentId { get; init; }
    public required double StartSec { get; init; }
    public required double EndSec { get; init; }
    public required string AudioPath { get; init; }
    public double VoicedProbability { get; init; }
    public double SilenceRatio { get; init; }
}

public sealed class AudioQualityMetrics
{
    public double Overall { get; set; }
    public double Noise { get; set; }
    public double MusicLeak { get; set; }
    public double Reverb { get; set; }
    public double Clipping { get; set; }
    public double Silence { get; set; }
}

public sealed class ProcessingStageRecord
{
    public required string Stage { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required string OutputPath { get; init; }
}
