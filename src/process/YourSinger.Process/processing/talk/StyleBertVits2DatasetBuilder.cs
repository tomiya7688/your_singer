using System.Text;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Talk;

public sealed class StyleBertVits2DatasetBuilder
{
    public const string StageVersion = "v1-11.1";

    private readonly UniversalVoiceDatasetRepository _datasetRepository;

    public StyleBertVits2DatasetBuilder(UniversalVoiceDatasetRepository datasetRepository)
    {
        _datasetRepository = datasetRepository;
    }

    public async Task<StyleBertVits2DatasetRecord> BuildAsync(
        ProjectWorkspace workspace,
        TrainingJobRecord job,
        string speakerName,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Talk)
            throw new InvalidOperationException("トーク学習ジョブのみStyle-Bert-VITS2 datasetを生成できます。");

        var dataset = await _datasetRepository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("Universal Voice Datasetがありません。");

        var selected = dataset.Segments
            .Where(x => x.SpeakerId == job.SpeakerId)
            .Where(x => x.ContentType == SegmentContentType.Speech)
            .Where(x => job.SegmentIds.Contains(x.SegmentId, StringComparer.Ordinal))
            .ToArray();

        var root = Path.Combine(workspace.RootPath, "training", "style-bert-vits2", job.JobId);
        var rawDirectory = Path.Combine(root, "raw");
        Directory.CreateDirectory(rawDirectory);

        var items = new List<StyleBertVits2TrainingItem>();
        var esdLines = new List<string>();

        foreach (var segment in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(segment.AudioPath);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".wav";

            var safeId = Sanitize(segment.SegmentId);
            var fileName = safeId + extension;
            var destination = Path.Combine(rawDirectory, fileName);
            File.Copy(segment.AudioPath, destination, overwrite: true);

            var relative = Path.Combine("raw", fileName).Replace('\\', '/');
            esdLines.Add($"{relative}|{EscapeField(speakerName)}|JP|{EscapeField(segment.Transcript)}");

            items.Add(new StyleBertVits2TrainingItem
            {
                SegmentId = segment.SegmentId,
                AudioPath = destination,
                Transcript = segment.Transcript,
                Phonemes = segment.Phonemes.Select(x => x.Phoneme).ToList(),
                SpeakerName = speakerName,
                StyleProsody = new StyleProsodyRecord
                {
                    SpeakingRate = segment.StyleProsody.SpeakingRate,
                    PitchRangeSemitones = segment.StyleProsody.PitchRangeSemitones,
                    EnergyVariation = segment.StyleProsody.EnergyVariation,
                    PauseRatio = segment.StyleProsody.PauseRatio
                }
            });
        }

        await File.WriteAllLinesAsync(
            Path.Combine(root, "esd.list"),
            esdLines,
            new UTF8Encoding(false),
            cancellationToken);

        var record = new StyleBertVits2DatasetRecord
        {
            StageVersion = StageVersion,
            JobId = job.JobId,
            SpeakerId = job.SpeakerId,
            DatasetFingerprint = job.DatasetFingerprint,
            AutoCorrectionEnabled = job.AutoCorrectionEnabled,
            Items = items
        };

        await using var stream = File.Create(Path.Combine(root, "dataset.json"));
        await JsonSerializer.SerializeAsync(
            stream,
            record,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            },
            cancellationToken);

        return record;
    }

    private static string EscapeField(string value) =>
        value.Replace("|", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string Sanitize(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }
}
