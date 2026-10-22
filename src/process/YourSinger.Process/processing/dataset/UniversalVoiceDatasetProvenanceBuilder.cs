using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// {
//   責務: [
//     UniversalVoiceDatasetProvenanceBuilder: segment特徴の生成元と方式を来歴へ記録する
//   ]
//   処理: [
//     1: segmentの特徴ごとに生成来歴を追加する
//     2: 音素来歴を現在のアラインメント方式へ同期する
//   ]
//   フィールド: []
// }
internal static class UniversalVoiceDatasetProvenanceBuilder
{
    // {
    //   責務: [AddForSegment: segmentの抽出特徴ごとに生成来歴を追加する]
    //   処理: [1: 特徴名、生成方式、段階、キャッシュキーを来歴へ記録する]
    //   引数: [dataset: 来歴追加先, segment: 特徴抽出済みsegment, stageVersion: 抽出段階の版]
    //   戻り値: []
    //   副作用: [dataset.Provenanceへ来歴を追加する]
    // }
    internal static void AddForSegment(
        UniversalVoiceDatasetRecord dataset,
        UniversalVoiceSegment segment,
        string stageVersion)
    {
        // 学習・診断で追跡する特徴ごとに、生成方式とキャッシュ元を記録する。
        foreach (var (feature, method) in new[]
        {
            ("transcript", "faster-whisper"),
            ("phonemes", $"pyopenjtalk+{segment.AlignmentMethod}"),
            ("f0", "autocorrelation"),
            ("energy", "rms"),
            ("voicing", "pitch-confidence"),
            ("style_prosody", "summary-statistics"),
            ("speaker_embedding", "speaker-centroid")
        })
        {
            dataset.Provenance.Add(new FeatureProvenanceRecord
            {
                SegmentId = segment.SegmentId,
                FeatureName = feature,
                StageVersion = stageVersion,
                Method = method,
                CacheKey = segment.CacheKey
            });
        }
    }

    // {
    //   責務: [UpdatePhonemeMethods: 音素来歴へ各segmentの現在のアラインメント方式を反映する]
    //   処理: [1: segmentごとに最後の音素来歴を探し、方式の説明を更新する]
    //   引数: [dataset: 来歴を更新する共通データセット]
    //   戻り値: []
    //   副作用: [dataset.Provenance内の音素来歴を書き換える]
    // }
    internal static void UpdatePhonemeMethods(UniversalVoiceDatasetRecord dataset)
    {
        // 再解析されていないsegmentには来歴を新設せず、既存の記録だけを更新する。
        foreach (var segment in dataset.Segments)
        {
            var provenanceIndex = dataset.Provenance.FindLastIndex(item =>
                item.SegmentId == segment.SegmentId && item.FeatureName == "phonemes");
            if (provenanceIndex < 0)
            {
                continue;
            }

            var previous = dataset.Provenance[provenanceIndex];
            // 他の来歴フィールドを保持し、方式の記述だけを現状態へ同期する。
            dataset.Provenance[provenanceIndex] = new FeatureProvenanceRecord
            {
                SegmentId = previous.SegmentId,
                FeatureName = previous.FeatureName,
                StageVersion = previous.StageVersion,
                Method = $"pyopenjtalk+{segment.AlignmentMethod}",
                CacheKey = previous.CacheKey,
                GeneratedAt = previous.GeneratedAt
            };
        }
    }
}
