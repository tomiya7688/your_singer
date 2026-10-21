using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// {
//   責務: [
//     UniversalVoiceDatasetBuildContext: 1回のdataset構築で共有する読取済み状態を保持する
//   ]
//   フィールド: [
//     Dataset: 構築中のdataset
//     ExistingDataset: 再利用する既存dataset
//     ExistingSegmentsById: 既存segmentのキャッシュ検索表
//     SpeakerBySegment: segmentから話者IDへの割り当て
//     SpeakerEmbeddingById: 話者IDから埋め込みへの対応
//     ContentBySegment: segmentから内容分類への対応
//     TranscriptBySegment: segmentの最終文字起こし修正
//   ]
// }
internal sealed record UniversalVoiceDatasetBuildContext(
    UniversalVoiceDatasetRecord Dataset,
    UniversalVoiceDatasetRecord? ExistingDataset,
    Dictionary<string, UniversalVoiceSegment> ExistingSegmentsById,
    Dictionary<string, string> SpeakerBySegment,
    Dictionary<string, List<double>> SpeakerEmbeddingById,
    Dictionary<string, SegmentContentClassification> ContentBySegment)
{
    internal Dictionary<string, string> TranscriptBySegment { get; } =
        UniversalVoiceDatasetOverrideApplicator.LatestTranscriptValues(Dataset);
}
