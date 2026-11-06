namespace YourSinger.Data.Models;

// 共有話者特徴を学習に実際に使ったか、使えなかったかの記録(ジョブに残す)。
// Support は supported / unsupported。実利用が無いときに「相互学習済み」と表示しないための根拠。
public sealed class SharedFeatureUsageEntry
{
    public required string Feature { get; init; }
    public required string Domain { get; init; }
    public required string Support { get; init; }
    // 利用者向けの日本語の理由。
    public required string Reason { get; init; }
    // 学習に実際に使ったか。v1では直接conditioningを行わないため常に false。
    public required bool Used { get; init; }
}
