namespace YourSinger.Data.Models;

public sealed class UniversalVoiceDatasetRecord
{
    public required string SchemaVersion { get; init; }
    public required string StageVersion { get; init; }
    public List<UniversalVoiceSegment> Segments { get; init; } = [];
    public CoverageRecord Coverage { get; init; } = new();
    public List<FeatureProvenanceRecord> Provenance { get; init; } = [];
    public List<UserFeatureOverrideRecord> Overrides { get; init; } = [];
}

public sealed class UniversalVoiceSegment
{
    public required string SegmentId { get; init; }
    public required string SourceId { get; init; }
    public string? SpeakerId { get; set; }
    public SegmentContentType ContentType { get; set; }
    public required string AudioPath { get; init; }
    public required string CacheKey { get; init; }

    public string Transcript { get; set; } = string.Empty;
    public double AsrConfidence { get; set; }

    public List<PhonemeTimingRecord> Phonemes { get; init; } = [];
    public double AlignmentConfidence { get; set; }

    public string F0FeaturePath { get; set; } = string.Empty;
    public string EnergyFeaturePath { get; set; } = string.Empty;
    public string VoicingFeaturePath { get; set; } = string.Empty;

    public double PitchReliability { get; set; }
    public double MeanF0Hz { get; set; }
    public double F0StdDevHz { get; set; }
    public double MeanEnergy { get; set; }
    public double VoicedRatio { get; set; }

    public StyleProsodyRecord StyleProsody { get; set; } = new();
    public List<double> SpeakerEmbedding { get; init; } = [];
}

public sealed class PhonemeTimingRecord
{
    public required string Phoneme { get; init; }
    public double StartSec { get; init; }
    public double EndSec { get; init; }
    public double Confidence { get; init; }
}

public sealed class StyleProsodyRecord
{
    public double SpeakingRate { get; set; }
    public double PitchRangeSemitones { get; set; }
    public double EnergyVariation { get; set; }
    public double PauseRatio { get; set; }
}

public sealed class CoverageRecord
{
    public Dictionary<string, int> PhonemeCounts { get; init; } =
        new(StringComparer.Ordinal);
    public Dictionary<string, int> ContentTypeCounts { get; init; } =
        new(StringComparer.Ordinal);
}

public sealed class FeatureProvenanceRecord
{
    public required string SegmentId { get; init; }
    public required string FeatureName { get; init; }
    public required string StageVersion { get; init; }
    public required string Method { get; init; }
    public required string CacheKey { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class UserFeatureOverrideRecord
{
    public required string SegmentId { get; init; }
    public required string Field { get; init; }
    public required string Value { get; init; }
    public DateTimeOffset ChangedAt { get; init; } = DateTimeOffset.UtcNow;
}
