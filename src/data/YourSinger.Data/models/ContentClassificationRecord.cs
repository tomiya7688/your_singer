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
    public List<SegmentContentClassification> Segments { get; init; } = [];
}

public sealed class SegmentContentClassification
{
    public required string SegmentId { get; init; }
    public required SegmentContentType PredictedType { get; set; }
    public SegmentContentType? UserOverrideType { get; set; }
    public double SpeechConfidence { get; set; }
    public double SingingConfidence { get; set; }
    public double Confidence { get; set; }
    public bool IncludeInTalkDataset { get; set; }
    public bool IncludeInSingingDataset { get; set; }

    public SegmentContentType EffectiveType =>
        UserOverrideType ?? PredictedType;
}
