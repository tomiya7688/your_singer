namespace YourSinger.Data.Models;

// {
//   責務: [AlignmentMeasurementReference: 実測JSONと入力音声の同一性を保持する]
//   フィールド: [Path: project相対パス, Sha256: 記録hash, AudioSha256: 音声hash]
// }
public sealed class AlignmentMeasurementReference
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required string AudioSha256 { get; init; }
}
