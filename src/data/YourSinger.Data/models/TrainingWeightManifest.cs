namespace YourSinger.Data.Models;

// 学習器へ渡す区間Weightの別ファイル(train_weights.json)。公式の入力一覧(esd.list)は変更しない。
// 学習に使う全itemとweightが1対1であることを検証できる。正本: docs/design/v1-training/contracts-and-weighting.md。
public sealed class TrainingWeightManifest
{
    public const string FileName = "train_weights.json";
    public const string CurrentSchemaVersion = "1";

    public required string SchemaVersion { get; init; }
    public required string JobId { get; init; }
    public required string PolicyVersion { get; init; }
    // 重みの元になった評価と学習方式を含む入力fingerprint(TrainingJobRecord.InputFingerprint)。
    public required string InputFingerprint { get; init; }
    // 学習に使うitem。SegmentId昇順。
    public required List<TrainingWeightManifestItem> Items { get; init; }
    // 学習に使わなかった区間(確認待ち・除外)。重みは持たず、理由だけを監査用に残す。
    public List<TrainingWeightManifestSkipped> Skipped { get; init; } = [];
}

public sealed class TrainingWeightManifestItem
{
    public required string SegmentId { get; init; }
    // 入力一覧(esd.list等)が参照する音声ファイル名。
    public required string AudioFilename { get; init; }
    public required double TrainingWeight { get; init; }
}

public sealed class TrainingWeightManifestSkipped
{
    public required string SegmentId { get; init; }
    public required string Eligibility { get; init; }
    public required List<string> ReasonCodes { get; init; }
}
