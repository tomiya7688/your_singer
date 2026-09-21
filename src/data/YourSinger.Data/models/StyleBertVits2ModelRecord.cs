namespace YourSinger.Data.Models;

public sealed class StyleBertVits2DatasetRecord
{
    public required string StageVersion { get; init; }
    public required string JobId { get; init; }
    public required string SpeakerId { get; init; }
    public required string DatasetFingerprint { get; init; }
    public bool AutoCorrectionEnabled { get; init; }
    public List<StyleBertVits2TrainingItem> Items { get; init; } = [];
}

public sealed class StyleBertVits2TrainingItem
{
    public required string SegmentId { get; init; }
    public required string AudioPath { get; init; }
    public required string Transcript { get; init; }
    public List<string> Phonemes { get; init; } = [];
    public required string SpeakerName { get; init; }
    public string Language { get; init; } = "JP";
    public StyleProsodyRecord StyleProsody { get; init; } = new();
}

public sealed class StyleBertVits2ExportRequest
{
    public required string ModelName { get; init; }
    public required string SpeakerId { get; init; }
    public required string ConfigJsonPath { get; init; }
    public required string ModelWeightsPath { get; init; }
    public required string StyleVectorsPath { get; init; }
}

public sealed class StyleBertVits2ExportResult
{
    public required string OutputDirectory { get; init; }
    public required string TargetVersion { get; init; }
    public List<string> GeneratedFiles { get; init; } = [];
}
