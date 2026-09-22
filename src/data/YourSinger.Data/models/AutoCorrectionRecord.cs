namespace YourSinger.Data.Models;

public enum CorrectionState
{
    Observed,
    WeakObserved,
    Estimated,
    Corrected,
    Rejected
}

// 旧データの状態名だけで、実際に補完が適用済みだと判定しない。
public enum CorrectionApplicationStatus
{
    Unknown,
    NotRequired,
    Applied,
    Deferred
}

public sealed class AutoCorrectionSettings
{
    public bool Enabled { get; set; } = true;
    public string StageVersion { get; set; } = "v1-08.3";
    public PitchCompletionSettings PitchCompletion { get; init; } = new();
}

public sealed class CorrectionRecord
{
    public required string SegmentId { get; init; }
    public string? SpeakerId { get; init; }
    public string? Phoneme { get; init; }
    public required CorrectionState State { get; init; }
    public required string Method { get; init; }
    public CorrectionApplicationStatus ApplicationStatus { get; init; }
    public double Confidence { get; init; }
    public string? OriginalValue { get; init; }
    public string? CorrectedValue { get; init; }
    public string? Reason { get; init; }
    public int? StartFrame { get; init; }
    public int? EndFrameExclusive { get; init; }
    public string? InputFingerprint { get; init; }
    public string? OriginalFeaturePath { get; init; }
    public string? ResultFeaturePath { get; init; }
    public List<double> OriginalValues { get; init; } = [];
    public List<double> ResultValues { get; init; } = [];
}

public sealed class CorrectedDatasetView
{
    public required bool CorrectionEnabled { get; init; }
    public List<UniversalVoiceSegment> Segments { get; init; } = [];
    // 互換性のため名前は維持する。適用の有無はApplicationStatusで判断する。
    public List<CorrectionRecord> AppliedCorrections { get; init; } = [];
    public string Fingerprint { get; init; } = string.Empty;
}
