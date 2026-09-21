namespace YourSinger.Data.Models;

public enum CorrectionState
{
    Observed,
    WeakObserved,
    Estimated,
    Corrected,
    Rejected
}

public sealed class AutoCorrectionSettings
{
    public bool Enabled { get; set; } = true;
    public string StageVersion { get; set; } = "v1-08.1";
}

public sealed class CorrectionRecord
{
    public required string SegmentId { get; init; }
    public string? Phoneme { get; init; }
    public required CorrectionState State { get; init; }
    public required string Method { get; init; }
    public double Confidence { get; init; }
    public string? OriginalValue { get; init; }
    public string? CorrectedValue { get; init; }
    public string? Reason { get; init; }
}

public sealed class CorrectedDatasetView
{
    public required bool CorrectionEnabled { get; init; }
    public List<UniversalVoiceSegment> Segments { get; init; } = [];
    public List<CorrectionRecord> AppliedCorrections { get; init; } = [];
    public string Fingerprint { get; init; } = string.Empty;
}
