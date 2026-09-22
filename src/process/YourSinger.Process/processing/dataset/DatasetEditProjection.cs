using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Dataset;

/// <summary>保存済みの観測データを変更せず、ユーザー編集を学習用の読み取り結果へ反映する。</summary>
public static class DatasetEditProjection
{
    public static async Task<UniversalVoiceDatasetRecord> LoadAsync(
        ProjectWorkspace workspace,
        UniversalVoiceDatasetRepository repository,
        CancellationToken cancellationToken = default)
    {
        var dataset = await repository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("共通音声データセットがありません。");
        if (dataset.Segments.Select(x => x.SegmentId).Distinct(StringComparer.Ordinal).Count() != dataset.Segments.Count)
            throw new InvalidDataException("共通音声データセットに重複する区間IDがあります。");

        var speakers = await new SpeakerAnalysisRepository().LoadAsync(workspace, cancellationToken);
        var classifications = await new ContentClassificationRepository().LoadAsync(workspace, cancellationToken);
        var speakerBySegment = speakers?.Assignments.ToDictionary(x => x.SegmentId, x => x.SpeakerId, StringComparer.Ordinal);
        var speakerById = speakers?.Speakers.ToDictionary(x => x.SpeakerId, StringComparer.Ordinal);
        var classificationBySegment = classifications?.Classifications.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);

        if (speakers is not null)
            dataset.Segments.RemoveAll(x => speakers.ExcludedSegmentIds.Contains(x.SegmentId));

        foreach (var segment in dataset.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (speakerBySegment is not null && speakerBySegment.TryGetValue(segment.SegmentId, out var speakerId))
            {
                segment.SpeakerId = speakerId;
                segment.SpeakerEmbedding.Clear();
                if (speakerById is not null && speakerById.TryGetValue(speakerId, out var speaker))
                    segment.SpeakerEmbedding.AddRange(speaker.EmbeddingCentroid);
            }
            if (classificationBySegment is not null && classificationBySegment.TryGetValue(segment.SegmentId, out var classification))
                segment.ContentType = classification.ContentType;
        }

        var byId = dataset.Segments.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);
        foreach (var edit in dataset.Overrides.OrderBy(x => x.ChangedAt))
            if (edit.Field == "transcript" && byId.TryGetValue(edit.SegmentId, out var segment))
                segment.Transcript = edit.Value;

        dataset.Coverage.PhonemeCounts.Clear();
        dataset.Coverage.ContentTypeCounts.Clear();
        foreach (var segment in dataset.Segments)
        {
            var key = segment.ContentType.ToString().ToLowerInvariant();
            dataset.Coverage.ContentTypeCounts[key] = dataset.Coverage.ContentTypeCounts.GetValueOrDefault(key) + 1;
            foreach (var phoneme in segment.Phonemes)
                dataset.Coverage.PhonemeCounts[phoneme.Phoneme] = dataset.Coverage.PhonemeCounts.GetValueOrDefault(phoneme.Phoneme) + 1;
        }
        return dataset;
    }
}
