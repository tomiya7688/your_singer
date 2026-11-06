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
    // 共有話者特徴(トーク・歌唱の相互利用)をBackendが受け入れたかの記録。null は未評価。
    // 未対応の特徴は Used=false と理由を残し、相互学習済みと表示しない根拠にする。
    public List<SharedFeatureUsageEntry>? SharedFeatureUsage { get; set; }
    // 計画時に選んだ学習戦略・プリセット・棚卸しの参照(登録の追加・削除があっても再現できる)。null は未選択(旧ジョブ含む)。
    public TrainingStrategyReference? Strategy { get; set; }
    // Preflight(実行前の解決)の結果。null は未実施。実行できる状態(Executable)かつ棚卸しが最新のときだけ学習を始められる。
    public TrainingResolvedPlanRecord? ResolvedPlan { get; set; }
    // worker の実行記録を指定と照合した結果。null は未照合(旧ジョブを含む)。
    public TrainingRunVerificationRecord? RunVerification { get; set; }
    // 学習方式の契約を持たない旧ジョブか。
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLegacyWithoutTrainingContract => RequestedTrainingMode is null;
}

// 実行前に解決した計画の保存形。worker は再解決せず、この内容のとおりに実行する(正本: training-modes-and-backends.md §3)。
public sealed class TrainingResolvedPlanRecord
{
    public required string ManifestHash { get; init; }
    public required string InventoryRevision { get; init; }
    public required bool Executable { get; init; }
    public string? StrategyId { get; init; }
    public string? StrategyVersion { get; init; }
    public string? Variant { get; init; }
    // 単一Trainer工程に対する互換記録(fine_tune / from_scratch)。工程が無ければ null。
    public string? ResolvedTrainingMode { get; init; }
    public List<TrainingResolvedStageRecord> Stages { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    // 実行できないときの理由(候補ごとの停止理由を平らにしたもの)。
    public List<string> StopReasons { get; init; } = [];
    public DateTimeOffset ResolvedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class TrainingResolvedStageRecord
{
    public required string StageId { get; init; }
    public required string OperationId { get; init; }
    public required string Mode { get; init; }
    public string? Backend { get; init; }
    public string? BackendRevision { get; init; }
    public TrainingInitialization? Initialization { get; init; }
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
