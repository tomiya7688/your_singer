namespace YourSinger.Data.Models;

// worker が実際に行った学習の記録(run_manifest.json の内容)。大きなバイナリは載せず、成果物は相対パスとsha256で表す。
public sealed class TrainingRunReport
{
    public const string CurrentSchemaVersion = "1";

    public string? SchemaVersion { get; init; }
    public string? JobId { get; init; }
    // 要求された解決済みモード(fine_tune / from_scratch)。worker が勝手に変えていないことの確認に使う。
    public string? ResolvedMode { get; init; }
    public int? Seed { get; init; }
    public int? TrainerExitCode { get; init; }
    // 実際に読み込んだ(または初期化した)重み。null は未観測で、指定どおりに初期化したとは主張できない。
    public TrainingInitialization? InitializationObserved { get; init; }
    public List<TrainingRunArtifact> Artifacts { get; init; } = [];
}

public sealed class TrainingRunArtifact
{
    public required string Kind { get; init; }
    // 学習出力ディレクトリからの相対パス。
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
}

public static class TrainingRunVerificationStatus
{
    // 指定(モード・seed・初期重み)と実行の記録が一致し、成果物のhashも一致した。
    public const string Verified = "verified";
    // 矛盾がある、または記録が欠けている。成功として扱わない。
    public const string Failed = "failed";
    // 矛盾はないが、初期重みの実読込が観測できていない。学習効果・指定どおりの初期化を確認済みとは表示しない。
    public const string Unverified = "unverified";
}

public sealed class TrainingRunVerificationRecord
{
    public required string Status { get; init; }
    public List<string> Problems { get; init; } = [];
    public DateTimeOffset VerifiedAt { get; init; } = DateTimeOffset.UtcNow;
}
