using System.Text;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Talk;

public sealed class StyleBertVits2DatasetBuilder
{
    public const string StageVersion = "v1-11.2";
    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    public StyleBertVits2DatasetBuilder(UniversalVoiceDatasetRepository datasetRepository) => _datasetRepository = datasetRepository;

    public async Task<StyleBertVits2DatasetRecord> BuildAsync(ProjectWorkspace workspace, TrainingJobRecord job,
        string speakerName, CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Talk)
            throw new InvalidOperationException("トーク学習ジョブのみトーク用データを生成できます。");
        if (string.IsNullOrWhiteSpace(speakerName))
            throw new ArgumentException("話者名が空です。", nameof(speakerName));
        var selected = await TrainingDatasetResolver.ResolveAsync(workspace, _datasetRepository, job, cancellationToken);
        var inputs = selected.Select(x => (Segment: x,
            Path: Path.GetFullPath(Path.Combine(workspace.RootPath, x.AudioPath)))).ToArray();
        foreach (var input in inputs)
        {
            if (!File.Exists(input.Path)) throw new FileNotFoundException("学習用音声がありません。", input.Path);
            if (string.IsNullOrWhiteSpace(input.Segment.Transcript))
                throw new InvalidDataException($"区間 {input.Segment.SegmentId} の文章が空です。");
        }
        // ジョブ全体は置換しない。既存のtrainerログや学習済み重みを保護する。
        var root = Path.Combine(workspace.RootPath, "training", "style-bert-vits2", job.JobId, "dataset");
        var staging = root + $".{Guid.NewGuid():N}.tmp";
        var backup = root + $".{Guid.NewGuid():N}.bak";
        var items = new List<StyleBertVits2TrainingItem>();
        var lines = new List<string>();
        Directory.CreateDirectory(Path.Combine(staging, "raw"));
        try
        {
            for (var index = 0; index < inputs.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (segment, inputPath) = inputs[index];
                var fileName = $"segment_{index:D6}{Path.GetExtension(inputPath)}";
                File.Copy(inputPath, Path.Combine(staging, "raw", fileName));
                var destination = Path.Combine(root, "raw", fileName);
                lines.Add($"raw/{fileName}|{EscapeField(speakerName)}|JP|{EscapeField(segment.Transcript)}");
                items.Add(new()
                {
                    SegmentId = segment.SegmentId, AudioPath = destination, Transcript = segment.Transcript,
                    Phonemes = segment.Phonemes.Select(x => x.Phoneme).ToList(), SpeakerName = speakerName,
                    StyleProsody = new()
                    {
                        SpeakingRate = segment.StyleProsody.SpeakingRate,
                        PitchRangeSemitones = segment.StyleProsody.PitchRangeSemitones,
                        EnergyVariation = segment.StyleProsody.EnergyVariation,
                        PauseRatio = segment.StyleProsody.PauseRatio
                    }
                });
            }
            await File.WriteAllLinesAsync(Path.Combine(staging, "esd.list"), lines, new UTF8Encoding(false), cancellationToken);
            var record = new StyleBertVits2DatasetRecord
            {
                StageVersion = StageVersion, JobId = job.JobId, SpeakerId = job.SpeakerId,
                DatasetFingerprint = job.DatasetFingerprint, AutoCorrectionEnabled = job.AutoCorrectionEnabled, Items = items
            };
            await using (var stream = File.Create(Path.Combine(staging, "dataset.json")))
                await JsonSerializer.SerializeAsync(stream, record, new JsonSerializerOptions
                {
                    WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(root)) Directory.Move(root, backup);
            try { Directory.Move(staging, root); }
            catch
            {
                if (Directory.Exists(backup)) Directory.Move(backup, root);
                throw;
            }
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            return record;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static string EscapeField(string value) => value.Replace("|", " ", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
