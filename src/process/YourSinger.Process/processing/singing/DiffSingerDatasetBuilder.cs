using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

// {
//   責務: [DiffSingerDatasetBuilder: 学習ジョブからDiffSinger用入力データを作成する]
//   フィールド: [datasetRepository: 共通音声データの読み込み]
//   処理: [1: jobのsegmentと特徴量を検証しdataset.jsonへ保存する]
// }
public sealed class DiffSingerDatasetBuilder(UniversalVoiceDatasetRepository datasetRepository)
{
    public const string StageVersion = "v1-10.4";
    public const string RequiredAlignmentMethod = UniversalVoiceSegment.RequiredSingingTrainingAlignmentMethod;

    // {
    //   責務: [BuildAsync: 歌唱学習jobからDiffSinger用datasetを生成する]
    //   処理: [
    //     1: jobとforced alignmentを検証する
    //     2: 音声・特徴量の入力ファイルを確認する
    //     3: 学習項目を組み立てdataset.jsonを保存する
    //   ]
    //   引数: [workspace: 作業領域, job: 歌唱学習対象と設定]
    //   戻り値: [record: 生成したDiffSinger用dataset]
    //   エラー: [job、alignment、入力ファイルが不正な場合に例外を送出する]
    // }
    public async Task<DiffSingerDatasetRecord> BuildAsync(ProjectWorkspace workspace, TrainingJobRecord job,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱学習ジョブのみ歌唱用データを生成できます。");
        // 保存済みjobのsegmentを再解決し、計画後にalignmentが変わっていないか検証する。
        var selected = await TrainingDatasetSnapshotService.ResolveJobAsync(datasetRepository, workspace, job, cancellationToken);
        var unaligned = selected.FirstOrDefault(x => !x.HasValidSingingTrainingAlignment());
        if (unaligned is not null)
            throw new InvalidDataException(
                $"セグメント {unaligned.SegmentId} に検証済みの音素単位forced alignmentがありません。" +
                "音素時刻が推定値の素材はDiffSinger学習に使用できません。");
        string RequireInput(string relative)
        {
            // workspace相対パスを絶対化してから存在確認し、保存するrecordには解決済みパスを含める。
            if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("歌唱学習の入力パスが空です。");
            var path = Path.GetFullPath(relative, workspace.RootPath);
            if (!File.Exists(path)) throw new FileNotFoundException("歌唱学習の入力ファイルがありません。", path);
            return path;
        }
        var record = new DiffSingerDatasetRecord
        {
            StageVersion = StageVersion, JobId = job.JobId, SpeakerId = job.SpeakerId,
            DatasetFingerprint = job.DatasetFingerprint, AutoCorrectionEnabled = job.AutoCorrectionEnabled,
            Items = selected.Select(x => new DiffSingerTrainingItem
            {
                SegmentId = x.SegmentId, AudioPath = RequireInput(x.AudioPath), Transcript = x.Transcript,
                Phonemes = x.Phonemes.Select(p => p.Phoneme).ToList(),
                DurationsSec = x.Phonemes.Select(p => Math.Max(0, p.EndSec - p.StartSec)).ToList(),
                AlignmentMethod = x.AlignmentMethod,
                PhonemeStartsSec = x.Phonemes.Select(p => p.StartSec).ToList(),
                PhonemeEndsSec = x.Phonemes.Select(p => p.EndSec).ToList(),
                F0FeaturePath = RequireInput(x.F0FeaturePath), EnergyFeaturePath = RequireInput(x.EnergyFeaturePath),
                MeanF0Hz = x.MeanF0Hz, PitchReliability = x.PitchReliability
            }).ToList()
        };
        var root = Path.Combine(workspace.RootPath, "training", "diffsinger", TrainingInputDirectory.RequireComponent(job.JobId));
        return await TrainingInputDirectory.BuildAsync(root, async staging =>
        {
            await using var stream = File.Create(Path.Combine(staging, "dataset.json"));
            await JsonSerializer.SerializeAsync(stream, record,
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }, cancellationToken);
            return record;
        }, cancellationToken);
    }
}
