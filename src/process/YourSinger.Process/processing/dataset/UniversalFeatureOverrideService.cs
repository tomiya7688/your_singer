using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Dataset;

public sealed class UniversalFeatureOverrideService
{
    private readonly UniversalVoiceDatasetRepository _repository;

    public UniversalFeatureOverrideService(
        UniversalVoiceDatasetRepository repository)
    {
        _repository = repository;
    }

    // {
    //   責務: [
    //     SetTranscriptAsync: segmentの文字起こしを修正し依存する音素結果を無効化する
    //   ]
    //   処理: [
    //     1: データセットと対象segmentを読み込む
    //     2: 文字起こしを保存し古い音素時刻と来歴を無効化する
    //     3: 音素・内容種別のカバレッジを再集計して保存する
    //   ]
    //   引数: [
    //     workspace: 対象プロジェクト
    //     segmentId: 修正するsegmentのID
    //     transcript: 新しい文字起こし
    //     cancellationToken: 処理中断要求
    //   ]
    //   戻り値: []
    //   エラー: [
    //     データセットまたは対象segmentがない場合に失敗する
    //   ]
    //   副作用: [
    //     Universal Voice Datasetを保存する
    //   ]
    // }
    public async Task SetTranscriptAsync(
        ProjectWorkspace workspace,
        string segmentId,
        string transcript,
        CancellationToken cancellationToken = default)
    {
        // 修正前の状態を読み込み、存在しないプロジェクトでは更新を止める。
        var dataset = await _repository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException(
                "Universal Voice Datasetがありません。");

        // 対象segmentを特定し、無効なIDを保存前に拒否する。
        var segment = dataset.Segments.FirstOrDefault(x => x.SegmentId == segmentId)
            ?? throw new KeyNotFoundException(
                $"対象セグメントが見つかりません: {segmentId}");

        // 文字起こしを変更したら、以前の音素列と時刻を再利用しない。
        segment.Transcript = transcript;
        segment.Phonemes.Clear();
        segment.AlignmentMeasurement = null;
        segment.AlignmentConfidence = 0;
        segment.AlignmentMethod = "invalidated-by-transcript-override";
        segment.AlignmentWarning = "文字起こしの変更後に音素境界の再解析が必要です。";
        var provenanceIndex = dataset.Provenance.FindLastIndex(x =>
            x.SegmentId == segmentId && x.FeatureName == "phonemes");
        if (provenanceIndex >= 0)
        {
            var previous = dataset.Provenance[provenanceIndex];
            dataset.Provenance[provenanceIndex] = new FeatureProvenanceRecord
            {
                SegmentId = previous.SegmentId,
                FeatureName = previous.FeatureName,
                StageVersion = previous.StageVersion,
                Method = segment.AlignmentMethod,
                CacheKey = previous.CacheKey,
                GeneratedAt = previous.GeneratedAt
            };
        }
        // 再解析でも同じ修正を再適用できるよう、利用者の入力を記録する。
        dataset.Overrides.Add(new UserFeatureOverrideRecord
        {
            SegmentId = segmentId,
            Field = "transcript",
            Value = transcript
        });

        // 保存済みsegmentから派生カバレッジを同期してからデータセットを保存する。
        UniversalVoiceDatasetCoverageBuilder.Rebuild(dataset);
        await _repository.SaveAsync(workspace, dataset, cancellationToken);
    }
}
