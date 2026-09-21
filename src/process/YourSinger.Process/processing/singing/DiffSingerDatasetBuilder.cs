using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Singing;

public sealed class DiffSingerDatasetBuilder
{
    public const string StageVersion = "v1-10.1";

    private readonly UniversalVoiceDatasetRepository _datasetRepository;

    public DiffSingerDatasetBuilder(UniversalVoiceDatasetRepository datasetRepository)
    {
        _datasetRepository = datasetRepository;
    }

    public async Task<DiffSingerDatasetRecord> BuildAsync(
        ProjectWorkspace workspace,
        TrainingJobRecord job,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱学習ジョブのみDiffSinger datasetを生成できます。");

        var dataset = await _datasetRepository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("Universal Voice Datasetがありません。");

        var selected = dataset.Segments
            .Where(x => x.SpeakerId == job.SpeakerId)
            .Where(x => x.ContentType == SegmentContentType.Singing)
            .Where(x => job.SegmentIds.Contains(x.SegmentId, StringComparer.Ordinal))
            .ToArray();

        var record = new DiffSingerDatasetRecord
        {
            StageVersion = StageVersion,
            JobId = job.JobId,
            SpeakerId = job.SpeakerId,
            DatasetFingerprint = job.DatasetFingerprint,
            AutoCorrectionEnabled = job.AutoCorrectionEnabled,
            Items = selected.Select(x => new DiffSingerTrainingItem
            {
                SegmentId = x.SegmentId,
                AudioPath = x.AudioPath,
                Transcript = x.Transcript,
                Phonemes = x.Phonemes.Select(p => p.Phoneme).ToList(),
                DurationsSec = x.Phonemes.Select(p => Math.Max(0, p.EndSec - p.StartSec)).ToList(),
                F0FeaturePath = x.F0FeaturePath,
                EnergyFeaturePath = x.EnergyFeaturePath,
                MeanF0Hz = x.MeanF0Hz,
                PitchReliability = x.PitchReliability
            }).ToList()
        };

        var directory = Path.Combine(workspace.RootPath, "training", "diffsinger", job.JobId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "dataset.json");
        var temp = path + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                record,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                },
                cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
        return record;
    }
}
