using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

public sealed class DiffSingerDatasetBuilder(UniversalVoiceDatasetRepository datasetRepository)
{
    public const string StageVersion = "v1-10.2";

    public async Task<DiffSingerDatasetRecord> BuildAsync(ProjectWorkspace workspace, TrainingJobRecord job,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱学習ジョブのみ歌唱用データを生成できます。");
        var selected = await TrainingDatasetSnapshotService.ResolveJobAsync(datasetRepository, workspace, job, cancellationToken);
        string RequireInput(string relative)
        {
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
