namespace YourSinger.Data.Models;

public enum SegmentContentType
{
    Speech,
    Singing,
    Ambiguous
}

public sealed class ContentClassificationRecord
{
    public required string StageVersion { get; init; }
    public List<SegmentContentClassification> Classifications { get; init; } = [];
    public List<ContentClassificationOverride> Overrides { get; init; } = [];
}

public sealed class SegmentContentClassification
{
    public required string SegmentId { get; init; }
    public SegmentContentType ContentType { get; set; }
    public double Confidence { get; set; }
    public double SpeechScore { get; set; }
    public double SingingScore { get; set; }
    public bool UserOverridden { get; set; }
}

public sealed class ContentClassificationOverride
{
    public required string SegmentId { get; init; }
    public required SegmentContentType OriginalType { get; init; }
    public required SegmentContentType NewType { get; init; }
    public DateTimeOffset ChangedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class UsageDatasetSelection
{
    public List<string> TalkSegmentIds { get; init; } = [];
    public List<string> SingingSegmentIds { get; init; } = [];
    public List<string> AmbiguousSegmentIds { get; init; } = [];
}
