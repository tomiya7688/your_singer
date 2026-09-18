namespace YourSinger.Data.Models;

public sealed class SpeakerAnalysisRecord
{
    public required string StageVersion { get; init; }
    public List<SpeakerRecord> Speakers { get; init; } = [];
    public List<SpeakerSegmentAssignment> Assignments { get; init; } = [];
    public List<SpeakerMergeRecord> MergeHistory { get; init; } = [];
    public HashSet<string> ExcludedSegmentIds { get; init; } = [];
}

public sealed class SpeakerRecord
{
    public required string SpeakerId { get; init; }
    public required string DisplayName { get; set; }
    public List<string> RepresentativeSegmentIds { get; init; } = [];
    public double TotalDurationSec { get; set; }
    public double UsableDurationSec { get; set; }
    public List<double> EmbeddingCentroid { get; init; } = [];
    public List<string> MergedFrom { get; init; } = [];
    public bool UserSelected { get; set; }
}

public sealed class SpeakerSegmentAssignment
{
    public required string SegmentId { get; init; }
    public required string SpeakerId { get; set; }
    public double Confidence { get; set; }
}

public sealed class SpeakerMergeRecord
{
    public required string TargetSpeakerId { get; init; }
    public required List<string> SourceSpeakerIds { get; init; }
    public DateTimeOffset MergedAt { get; init; } = DateTimeOffset.UtcNow;
}
