namespace YourSinger.Data.Models;

public enum CoverageQuality
{
    Good,
    Warning,
    Missing
}

public sealed class PhonemeCoverageDiagnostic
{
    public required string StageVersion { get; init; }
    public List<CoverageScopeDiagnostic> Scopes { get; init; } = [];
}

public sealed class CoverageScopeDiagnostic
{
    public string? SpeakerId { get; init; }
    public SegmentContentType? ContentType { get; init; }

    public Dictionary<string, int> PhonemeCounts { get; init; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, int> MoraCounts { get; init; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, int> ContextCounts { get; init; } =
        new(StringComparer.Ordinal);

    public List<string> MissingPhonemes { get; init; } = [];
    public List<string> LowCountPhonemes { get; init; } = [];
    public List<CoverageCategoryDiagnostic> Categories { get; init; } = [];
    public CoverageQuality Quality { get; set; }
}

public sealed class CoverageCategoryDiagnostic
{
    public required string Category { get; init; }
    public int Observed { get; init; }
    public int Expected { get; init; }
    public double Ratio { get; init; }
    public CoverageQuality Quality { get; init; }
}

public sealed class KanaCoverageItem
{
    public required string Kana { get; init; }
    public required string PhonemeKey { get; init; }
    public int Count { get; init; }
    public CoverageQuality Quality { get; init; }
}
