using System.Security.Cryptography;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Dataset;

/// <summary>観測データを保存し直さず、最新の手動編集を学習用の投影に適用する。</summary>
public sealed class TrainingDatasetSnapshotService(UniversalVoiceDatasetRepository datasetRepository)
{
    public async Task<UniversalVoiceDatasetRecord> LoadAsync(
        ProjectWorkspace workspace, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataset = await datasetRepository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("共通音声データがありません。");
        var speakers = await new SpeakerAnalysisRepository().LoadAsync(workspace, cancellationToken);
        var classification = await new ContentClassificationRepository().LoadAsync(workspace, cancellationToken);
        var speakerBySegment = speakers?.Assignments.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);
        var speakerById = speakers?.Speakers.ToDictionary(x => x.SpeakerId, StringComparer.Ordinal);
        var typeBySegment = classification?.Classifications.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);

        if (dataset.Segments.Select(x => x.SegmentId).Distinct(StringComparer.Ordinal).Count() != dataset.Segments.Count)
            throw new InvalidDataException("共通音声データに重複した区間IDがあります。");

        foreach (var segment in dataset.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (speakerBySegment is not null)
            {
                segment.SpeakerEmbedding.Clear();
                if (speakerBySegment.TryGetValue(segment.SegmentId, out var assignment) &&
                    speakerById!.TryGetValue(assignment.SpeakerId, out var speaker))
                {
                    segment.SpeakerId = speaker.SpeakerId;
                    segment.SpeakerEmbedding.AddRange(speaker.EmbeddingCentroid);
                }
                else
                {
                    // 割り当てが消えた古い話者へ素材を戻さない。
                    segment.SpeakerId = null;
                }
            }
            if (typeBySegment is not null)
                segment.ContentType = typeBySegment.TryGetValue(segment.SegmentId, out var item)
                    ? item.ContentType : SegmentContentType.Ambiguous;
        }
        if (speakers is not null)
            dataset.Segments.RemoveAll(x => speakers.ExcludedSegmentIds.Contains(x.SegmentId));

        return dataset;
    }

    public static string CreateFingerprint(UniversalVoiceDatasetRecord dataset, bool enabled,
        IReadOnlyList<CorrectionRecord> corrections, PitchCompletionSettings? pitchSettings = null)
    {
        return Hash(new
        {
            version = "training-snapshot-3", dataset.SchemaVersion, dataset.StageVersion,
            correctionVersion = AutoCorrectionService.StageVersion, enabled, pitchSettings,
            segments = dataset.Segments.OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToArray(),
            corrections = corrections.OrderBy(x => x.SegmentId, StringComparer.Ordinal)
                .ThenBy(x => x.SpeakerId, StringComparer.Ordinal).ThenBy(x => x.Phoneme, StringComparer.Ordinal)
                .ThenBy(x => x.StartFrame).ThenBy(x => x.State).ThenBy(x => x.Method, StringComparer.Ordinal)
                .ThenBy(x => x.Confidence).ThenBy(x => x.OriginalValue, StringComparer.Ordinal)
                .ThenBy(x => x.CorrectedValue, StringComparer.Ordinal).ToArray()
        });
    }

    public static string CreateJobFingerprint(string snapshotFingerprint, string speakerId,
        TrainingTarget target, IEnumerable<string> segmentIds) => Hash(new
    {
        version = "training-job-input-2", snapshotFingerprint, speakerId, target,
        segmentIds = segmentIds.Order(StringComparer.Ordinal).ToArray()
    });

    public static async Task<IReadOnlyList<UniversalVoiceSegment>> ResolveJobAsync(
        UniversalVoiceDatasetRepository repository, ProjectWorkspace workspace, TrainingJobRecord job,
        CancellationToken cancellationToken = default)
    {
        if (job.Target is not (TrainingTarget.Talk or TrainingTarget.Singing))
            throw new InvalidOperationException("学習用途が不正です。");
        var view = await new AutoCorrectionService(repository, new AutoCorrectionRepository())
            .BuildAsync(workspace, job.AutoCorrectionEnabled, cancellationToken);
        var ids = job.SegmentIds.ToHashSet(StringComparer.Ordinal);
        var contentType = job.Target == TrainingTarget.Talk ? SegmentContentType.Speech : SegmentContentType.Singing;
        var selected = view.Segments.Where(x => ids.Contains(x.SegmentId) &&
                x.SpeakerId == job.SpeakerId && x.ContentType == contentType)
            .OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToArray();
        if (ids.Count == 0 || ids.Count != job.SegmentIds.Count || selected.Length != ids.Count ||
            CreateJobFingerprint(view.Fingerprint, job.SpeakerId, job.Target, ids) != job.DatasetFingerprint)
            throw new InvalidOperationException("学習入力が変更されています。最新の設定で学習ジョブを作り直してください。");
        return selected;
    }

    private static string Hash<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
