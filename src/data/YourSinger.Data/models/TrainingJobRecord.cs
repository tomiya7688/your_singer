namespace YourSinger.Data.Models;

[Flags]
public enum TrainingTarget
{
    None = 0,
    Talk = 1,
    Singing = 2,
    Both = Talk | Singing
}

public enum TrainingJobState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Canceled
}

public sealed class SpeakerTrainingSelection
{
    public required string SpeakerId { get; init; }
    public TrainingTarget Target { get; init; } = TrainingTarget.Both;
    public bool AutoCorrectionEnabled { get; init; } = true;
}

public sealed class TrainingJobRecord
{
    public required string JobId { get; init; }
    public required string SpeakerId { get; init; }
    public required TrainingTarget Target { get; init; }
    public bool AutoCorrectionEnabled { get; init; }
    public required string DatasetFingerprint { get; init; }
    public TrainingJobState State { get; set; } = TrainingJobState.Pending;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> SegmentIds { get; init; } = [];
    public List<TrainingArtifactRecord> Artifacts { get; init; } = [];
}

public sealed class TrainingArtifactRecord
{
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public string? Format { get; init; }
    public string? Version { get; init; }
}

public sealed class TrainingJobBatch
{
    public required string BatchId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<TrainingJobRecord> Jobs { get; init; } = [];
}
