namespace YourSinger.Data.Models;

// 初期化方式のユーザー指定(auto / fine_tune / from_scratch)。JSONでは snake_case 文字列で保存する。
// 正本: docs/design/v1-training/training-modes-and-backends.md「1. 学習モード契約」「2. ジョブ保存契約」。
public enum TrainingMode
{
    Auto,
    FineTune,
    FromScratch
}

// 目標質感の要求。presetは文字列IDで保存し、語彙の検証は質感Registry側(#136)に置く。
public sealed class TrainingTextureRequest
{
    public required string Preset { get; init; }
    public string? Description { get; init; }
    // 同一話者の参考区間ID。声質参照として使う区間。
    public List<string> ReferenceSegmentIds { get; init; } = [];
}

public sealed class TrainingInitializationComponent
{
    public required string Name { get; init; }
    public required string Sha256 { get; init; }
    // ProjectWorkspace範囲内の相対パス。
    public required string Path { get; init; }
}

// 初期化情報。scratch は random_initialization で components は空、fine_tune は pretrained_checkpoint で components が必須。
public sealed class TrainingInitialization
{
    public required string Type { get; init; }
    public List<TrainingInitializationComponent> Components { get; init; } = [];
}

// ジョブ作成時にユーザーが指定する用途別の設定。未指定の用途は既定(auto・natural)になる。
public sealed class TrainingJobOptions
{
    public TrainingTextureRequest? Texture { get; init; }
    public TrainingMode Mode { get; init; } = TrainingMode.Auto;
    public int? Seed { get; init; }
    // fine_tune のときの初期重み。from_scratch では指定できない。
    public List<TrainingInitializationComponent> BaseCheckpoints { get; init; } = [];
}

public static class TrainingJobVocabulary
{
    public const string PretrainedCheckpoint = "pretrained_checkpoint";
    public const string RandomInitialization = "random_initialization";
    public const string DefaultTexturePreset = "natural";
}
