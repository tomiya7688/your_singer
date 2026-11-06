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
    // 用途別の設定。未指定(null)は既定(auto・natural)で作成する。Both は用途ごとに独立して指定できる。
    public TrainingJobOptions? TalkOptions { get; init; }
    public TrainingJobOptions? SingingOptions { get; init; }
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
    // 以下は学習方式の契約。この項目がない旧ジョブは null で、新規学習の成功として扱わない(再作成が必要)。
    public TrainingTextureRequest? RequestedTexture { get; init; }
    public TrainingMode? RequestedTrainingMode { get; init; }
    // 単一Trainer工程の解決済みモード。auto は解決前なので null。解決後に auto は入らない。
    public TrainingMode? ResolvedTrainingMode { get; init; }
    public TrainingInitialization? Initialization { get; init; }
    public int? Seed { get; init; }
    // 学習入力のfingerprint(training-input-v1)と、使うbackendの識別・固定revision。旧ジョブは null。
    public string? InputFingerprint { get; set; }
    public string? Backend { get; set; }
    public string? BackendRevision { get; set; }
    // 学習方式の契約を持たない旧ジョブか。
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLegacyWithoutTrainingContract => RequestedTrainingMode is null;
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
    // 空文字は、この項目がない旧形式の保存データを示す。
    public string InputVersion { get; init; } = string.Empty;
    public string? RecreatedFromBatchId { get; init; }
    public List<TrainingJobRecord> Jobs { get; init; } = [];
}

public sealed record TrainingBatchLoadError(string FileName, string Message);

public sealed class TrainingBatchHistory
{
    public List<TrainingJobBatch> Batches { get; init; } = [];
    public List<TrainingBatchLoadError> Errors { get; init; } = [];
}
