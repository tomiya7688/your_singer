using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

public sealed class DiffSingerDatasetBuilder
{
    public const string StageVersion = "v1-10.2";
    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    public DiffSingerDatasetBuilder(UniversalVoiceDatasetRepository datasetRepository) => _datasetRepository = datasetRepository;

    public async Task<DiffSingerDatasetRecord> BuildAsync(ProjectWorkspace workspace, TrainingJobRecord job,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱学習ジョブのみ歌唱用データを生成できます。");
        var selected = await TrainingDatasetResolver.ResolveAsync(workspace, _datasetRepository, job, cancellationToken);
        var record = new DiffSingerDatasetRecord
        {
            StageVersion = StageVersion, JobId = job.JobId, SpeakerId = job.SpeakerId,
            DatasetFingerprint = job.DatasetFingerprint, AutoCorrectionEnabled = job.AutoCorrectionEnabled,
            Items = selected.Select(x => new DiffSingerTrainingItem
            {
                SegmentId = x.SegmentId, AudioPath = Path.GetFullPath(Path.Combine(workspace.RootPath, x.AudioPath)),
                Transcript = x.Transcript, Phonemes = x.Phonemes.Select(p => p.Phoneme).ToList(),
                DurationsSec = x.Phonemes.Select(p => Math.Max(0, p.EndSec - p.StartSec)).ToList(),
                F0FeaturePath = Path.GetFullPath(Path.Combine(workspace.RootPath, x.F0FeaturePath)),
                EnergyFeaturePath = Path.GetFullPath(Path.Combine(workspace.RootPath, x.EnergyFeaturePath)),
                MeanF0Hz = x.MeanF0Hz, PitchReliability = x.PitchReliability
            }).ToList()
        };
        foreach (var item in record.Items)
            foreach (var path in new[] { item.AudioPath, item.F0FeaturePath, item.EnergyFeaturePath })
                if (!File.Exists(path)) throw new FileNotFoundException("歌唱学習に必要なファイルがありません。", path);
        var directory = Path.Combine(workspace.RootPath, "training", "diffsinger", job.JobId);
        Directory.CreateDirectory(directory);
        var pathName = Path.Combine(directory, "dataset.json");
        var temp = pathName + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, record, new JsonSerializerOptions
                {
                    WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, pathName, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return record;
    }
}
