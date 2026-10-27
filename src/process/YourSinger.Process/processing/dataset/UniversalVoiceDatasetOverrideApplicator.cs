using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// {
//   責務: [
//     UniversalVoiceDatasetOverrideApplicator: 保存済みの利用者修正をデータセットへ適用する
//   ]
//   処理: [
//     1: 更新日時から選んだ最終の文字起こし修正を適用する
//     2: 文字起こし変更時は依存する音素時刻を無効化する
//   ]
//   フィールド: []
// }
internal static class UniversalVoiceDatasetOverrideApplicator
{
    // {
    //   責務: [Apply: 保存済み利用者修正を抽出済みsegmentへ再適用する]
    //   処理: [1: 対象segmentを特定し、最終の文字起こし修正だけを適用する]
    //   引数: [dataset: 修正を適用する共通データセット]
    //   戻り値: []
    //   副作用: [dataset内のsegmentの文字起こしと音素状態を書き換える]
    // }
    internal static void Apply(UniversalVoiceDatasetRecord dataset)
    {
        // 繰り返し検索を避け、segment IDで直接対象を解決する。
        var segmentsById = dataset.Segments.ToDictionary(segment => segment.SegmentId);

        // 過去の値を順に再適用すると最新歌詞で再解析した時刻も消えるため、最終値だけを使う。
        foreach (var (segmentId, transcript) in LatestTranscriptValues(dataset))
        {
            if (!segmentsById.TryGetValue(segmentId, out var segment))
            {
                continue;
            }

            // 文字起こしが変わる場合だけ古い音素列と時刻を破棄する。
            if (!string.Equals(segment.Transcript, transcript, StringComparison.Ordinal))
            {
                segment.Phonemes.Clear();
                segment.AlignmentMeasurement = null;
                segment.AlignmentConfidence = 0;
                segment.AlignmentMethod = "invalidated-by-transcript-override";
                segment.AlignmentWarning = "文字起こしの変更後に音素境界の再解析が必要です。";
            }

            segment.Transcript = transcript;
        }
    }

    // {
    //   責務: [LatestTranscriptValues: 修正履歴から区間ごとの最終歌詞を取り出す]
    //   処理: [1: 文字起こし修正を日時順に並べる 2: 各区間の最後の値を選ぶ]
    //   引数: [dataset: 修正履歴を持つデータセット]
    //   戻り値: [transcripts: 区間IDと最終歌詞の対応]
    // }
    internal static Dictionary<string, string> LatestTranscriptValues(UniversalVoiceDatasetRecord dataset) =>
        dataset.Overrides.Where(item => item.Field == "transcript").OrderBy(item => item.ChangedAt)
            .GroupBy(item => item.SegmentId).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
}
