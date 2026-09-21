namespace YourSinger.Data.Models;

public sealed class DiffSingerDatasetRecord
{
    public required string StageVersion { get; init; }
    public required string JobId { get; init; }
    public required string SpeakerId { get; init; }
    public required string DatasetFingerprint { get; init; }
    public bool AutoCorrectionEnabled { get; init; }
    public List<DiffSingerTrainingItem> Items { get; init; } = [];
}

public sealed class DiffSingerTrainingItem
{
    public required string SegmentId { get; init; }
    public required string AudioPath { get; init; }
    public required string Transcript { get; init; }
    public List<string> Phonemes { get; init; } = [];
    public List<double> DurationsSec { get; init; } = [];
    public required string F0FeaturePath { get; init; }
    public required string EnergyFeaturePath { get; init; }
    public double MeanF0Hz { get; init; }
    public double PitchReliability { get; init; }
}

public sealed class DiffSingerExportRequest
{
    public required string SingerName { get; init; }
    public required string SpeakerId { get; init; }
    public required string AcousticModelPath { get; init; }
    public required string DurationModelPath { get; init; }
    public required string DurationLinguisticModelPath { get; init; }
    public required string DurationDictionaryPath { get; init; }
    public string? PitchDirectory { get; init; }
    public string? VarianceDirectory { get; init; }
    public required string VocoderDirectory { get; init; }
    public required IReadOnlyList<string> Phonemes { get; init; }
}

public sealed class DiffSingerExportResult
{
    public required string OutputDirectory { get; init; }
    public required string OpenUtauVersion { get; init; }
    public List<string> GeneratedFiles { get; init; } = [];
}
