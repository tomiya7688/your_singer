namespace YourSinger.Data.Models;

public enum SingingQualityLevel
{
    Good,
    Warning,
    Insufficient
}

public sealed class SingingQualityDiagnostic
{
    public required string StageVersion { get; init; }
    public List<SingingQualityScope> Scopes { get; init; } = [];
}

public sealed class SingingQualityScope
{
    public string? SpeakerId { get; init; }
    public int SegmentCount { get; init; }
    public double TotalDurationSec { get; init; }

    public double MinF0Hz { get; init; }
    public double MaxF0Hz { get; init; }
    public double MeanPitchReliability { get; init; }

    public double LowRangeDurationSec { get; init; }
    public double MidRangeDurationSec { get; init; }
    public double HighRangeDurationSec { get; init; }

    public Dictionary<string, Dictionary<string, int>> VowelCoverageByRange { get; init; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, int> ConsonantVowelTransitions { get; init; } =
        new(StringComparer.Ordinal);

    public int SustainedVowelCount { get; init; }
    public double SustainedVowelDurationSec { get; init; }
    public double LongToneQuality { get; init; }

    public List<string> Warnings { get; init; } = [];
    public SingingQualityLevel Quality { get; init; }
}

public sealed class SingingCoverageSummary
{
    public int ScopeCount { get; set; }
    public SingingQualityLevel OverallQuality { get; set; }
}
