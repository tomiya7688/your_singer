using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// {
//   責務: [
//     UniversalVoiceDatasetCoverageBuilder: segmentからUniversal Voice Datasetの派生カバレッジを作る
//   ]
//   処理: [
//     1: 音素数と内容種別数を再集計する
//   ]
//   フィールド: []
// }
internal static class UniversalVoiceDatasetCoverageBuilder
{
    // {
    //   責務: [
    //     Rebuild: 保存済みsegmentから派生カバレッジを再集計する
    //   ]
    //   処理: [
    //     1: 既存の音素数と内容種別数を消去する
    //     2: segmentの内容種別と音素を数える
    //   ]
    //   引数: [
    //     dataset: 再集計する共通データセット
    //   ]
    //   戻り値: []
    //   副作用: [
    //     datasetのCoverageを書き換える
    //   ]
    // }
    internal static void Rebuild(UniversalVoiceDatasetRecord dataset)
    {
        dataset.Coverage.PhonemeCounts.Clear();
        dataset.Coverage.ContentTypeCounts.Clear();

        foreach (var segment in dataset.Segments)
        {
            // segment単位の内容種別数を更新する。
            var contentKey = segment.ContentType.ToString().ToLowerInvariant();
            dataset.Coverage.ContentTypeCounts[contentKey] =
                dataset.Coverage.ContentTypeCounts.GetValueOrDefault(contentKey) + 1;

            // segmentの保存済み音素から全体の音素出現数を更新する。
            foreach (var phoneme in segment.Phonemes)
            {
                dataset.Coverage.PhonemeCounts[phoneme.Phoneme] =
                    dataset.Coverage.PhonemeCounts.GetValueOrDefault(phoneme.Phoneme) + 1;
            }
        }
    }
}
