using System.Text;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Talk;

public sealed class StyleBertVits2DatasetBuilder(UniversalVoiceDatasetRepository datasetRepository)
{
    public const string StageVersion = "v1-11.2";

    public async Task<StyleBertVits2DatasetRecord> BuildAsync(ProjectWorkspace workspace, TrainingJobRecord job,
        string speakerName, CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Talk)
            throw new InvalidOperationException("トーク学習ジョブのみトーク用データを生成できます。");
        var selected = await TrainingDatasetSnapshotService.ResolveJobAsync(datasetRepository, workspace, job, cancellationToken);
        var root = Path.Combine(workspace.RootPath, "training", "style-bert-vits2", TrainingInputDirectory.RequireComponent(job.JobId));
        return await TrainingInputDirectory.BuildAsync(root, async staging =>
        {
            var rawDirectory = Path.Combine(staging, "raw");
            Directory.CreateDirectory(rawDirectory);
            var items = new List<StyleBertVits2TrainingItem>();
            var lines = new List<string>();
            foreach (var segment in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(segment.AudioPath, workspace.RootPath);
                var fileName = TrainingInputDirectory.RequireComponent(segment.SegmentId) + ".wav";
                File.Copy(source, Path.Combine(rawDirectory, fileName), overwrite: false);
                lines.Add($"raw/{fileName}|{EscapeField(speakerName)}|JP|{EscapeField(segment.Transcript)}");
                items.Add(new StyleBertVits2TrainingItem
                {
                    SegmentId = segment.SegmentId, AudioPath = Path.Combine(root, "raw", fileName),
                    Transcript = segment.Transcript, Phonemes = segment.Phonemes.Select(x => x.Phoneme).ToList(),
                    SpeakerName = speakerName,
                    StyleProsody = new StyleProsodyRecord
                    {
                        SpeakingRate = segment.StyleProsody.SpeakingRate,
                        PitchRangeSemitones = segment.StyleProsody.PitchRangeSemitones,
                        EnergyVariation = segment.StyleProsody.EnergyVariation, PauseRatio = segment.StyleProsody.PauseRatio
                    }
                });
            }
            await File.WriteAllLinesAsync(Path.Combine(staging, "esd.list"), lines, new UTF8Encoding(false), cancellationToken);
            var record = new StyleBertVits2DatasetRecord
            {
                StageVersion = StageVersion, JobId = job.JobId, SpeakerId = job.SpeakerId,
                DatasetFingerprint = job.DatasetFingerprint, AutoCorrectionEnabled = job.AutoCorrectionEnabled, Items = items
            };
            await using var stream = File.Create(Path.Combine(staging, "dataset.json"));
            await JsonSerializer.SerializeAsync(stream, record,
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }, cancellationToken);
            return record;
        }, cancellationToken);
    }

    private static string EscapeField(string value) => value.Replace("|", " ", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
