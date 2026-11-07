using System.Text.Json.Serialization;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [VerificationStageRecord: 検証の1工程の状態と理由]
//   フィールド: [State: not_run/running/passed/warning/failed/unavailable, Reasons: 理由コード]
// }
public sealed class VerificationStageRecord
{
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("reasons")] public List<string> Reasons { get; init; } = [];
}

// {
//   責務: [VerificationArtifactRecord: 検証で保存した成果物(試験音声・ログ)1件]
//   フィールド: [Kind: sample_wav など, Path: 検証ディレクトリからの相対パス, AudioSha256: 保存時の内容ハッシュ, Label: 試験文・音符などの説明]
// }
public sealed class VerificationArtifactRecord
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("audio_sha256")] public string? AudioSha256 { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
}

// {
//   責務: [CheckpointCandidateRecord: 選定対象のcheckpoint候補1件]
//   フィールド: [Id: 候補名, Sha256: 重みのハッシュ, Eligible: 適格か, Reason: 不適格または評価の理由]
// }
public sealed class CheckpointCandidateRecord
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
    [JsonPropertyName("eligible")] public bool Eligible { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

// {
//   責務: [CheckpointSelectionRecord: checkpoint選定の結果。未評価なら pending で選定を保留する]
//   フィールド: [State: selected/pending, SelectedId: 選ばれた候補, Reason: 選定理由, Candidates: 候補一覧]
// }
public sealed class CheckpointSelectionRecord
{
    [JsonPropertyName("state")] public string State { get; init; } = "pending";
    [JsonPropertyName("selected_id")] public string? SelectedId { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("candidates")] public List<CheckpointCandidateRecord> Candidates { get; init; } = [];
}

// {
//   責務: [TrainingVerificationRecord: training/verification/<job-id>/verification.json の読み取りモデル(設計: verification-and-release.md)]
//   フィールド: [SchemaVersion: 契約の版, JobId: 対象ジョブ, Backend: 学習器, CompatibilityTarget: 外部互換の対象, Stages: 工程名→状態, Artifacts: 保存した成果物, CheckpointSelection: 候補と選定]
// }
public sealed class TrainingVerificationRecord
{
    public const string SupportedSchemaVersion = "1";
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; init; } = "";
    [JsonPropertyName("job_id")] public string JobId { get; init; } = "";
    [JsonPropertyName("backend")] public string? Backend { get; init; }
    [JsonPropertyName("compatibility_target")] public string? CompatibilityTarget { get; init; }
    [JsonPropertyName("stages")] public Dictionary<string, VerificationStageRecord> Stages { get; init; } = [];
    [JsonPropertyName("artifacts")] public List<VerificationArtifactRecord> Artifacts { get; init; } = [];
    [JsonPropertyName("checkpoint_selection")] public CheckpointSelectionRecord? CheckpointSelection { get; init; }
}
