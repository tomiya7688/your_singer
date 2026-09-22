using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Speaker;

public sealed class SpeakerOverrideService
{
    private readonly SpeakerAnalysisRepository _repository;
    public SpeakerOverrideService(SpeakerAnalysisRepository repository) => _repository = repository;

    public async Task SetSelectedAsync(ProjectWorkspace workspace, string speakerId, bool selected,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        RequireSpeaker(record, speakerId).UserSelected = selected;
        await _repository.SaveAsync(workspace, record, cancellationToken);
    }

    public async Task ExcludeSegmentAsync(ProjectWorkspace workspace, string segmentId, bool excluded,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        if (!record.Assignments.Any(x => x.SegmentId == segmentId))
            throw new KeyNotFoundException($"話者の割り当てがない区間です: {segmentId}");
        if (excluded) record.ExcludedSegmentIds.Add(segmentId);
        else record.ExcludedSegmentIds.Remove(segmentId);
        await RecalculateDurationsAsync(workspace, record, cancellationToken);
        await _repository.SaveAsync(workspace, record, cancellationToken);
    }

    public async Task MergeSpeakersAsync(ProjectWorkspace workspace, string targetSpeakerId,
        IReadOnlyCollection<string> sourceSpeakerIds, CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        var target = RequireSpeaker(record, targetSpeakerId);
        var sources = sourceSpeakerIds.Where(id => id != targetSpeakerId).Distinct(StringComparer.Ordinal)
            .Select(id => RequireSpeaker(record, id)).ToArray();
        if (sources.Length == 0) return;
        await RecalculateDurationsAsync(workspace, record, cancellationToken);

        // 重心は各話者の実時間で重み付けした近似。元の区間埋め込みの再抽出は行わない。
        var members = new[] { target }.Concat(sources).ToArray();
        var size = target.EmbeddingCentroid.Count;
        if (size > 0 && members.All(x => x.EmbeddingCentroid.Count == size))
        {
            var centroid = Enumerable.Range(0, size)
                .Select(i => members.Sum(x => x.EmbeddingCentroid[i] * x.TotalDurationSec)).ToArray();
            var norm = Math.Sqrt(centroid.Sum(x => x * x));
            if (norm > 0)
            {
                target.EmbeddingCentroid.Clear();
                target.EmbeddingCentroid.AddRange(centroid.Select(x => x / norm));
            }
        }
        var representatives = members.SelectMany(x => x.RepresentativeSegmentIds).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var source in sources)
        {
            foreach (var assignment in record.Assignments.Where(x => x.SpeakerId == source.SpeakerId))
                assignment.SpeakerId = targetSpeakerId;
            target.MergedFrom.Add(source.SpeakerId);
            target.MergedFrom.AddRange(source.MergedFrom);
            target.UserSelected |= source.UserSelected;
            record.Speakers.Remove(source);
        }
        var history = target.MergedFrom.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        target.MergedFrom.Clear();
        target.MergedFrom.AddRange(history);
        target.RepresentativeSegmentIds.Clear();
        target.RepresentativeSegmentIds.AddRange(representatives.Take(3));
        record.MergeHistory.Add(new()
        {
            TargetSpeakerId = targetSpeakerId, SourceSpeakerIds = sources.Select(x => x.SpeakerId).ToList()
        });
        await RecalculateDurationsAsync(workspace, record, cancellationToken);
        await _repository.SaveAsync(workspace, record, cancellationToken);
    }

    private async Task<SpeakerAnalysisRecord> RequireRecordAsync(ProjectWorkspace workspace, CancellationToken cancellationToken) =>
        await _repository.LoadAsync(workspace, cancellationToken)
        ?? throw new InvalidOperationException("話者解析結果がありません。");

    private static SpeakerRecord RequireSpeaker(SpeakerAnalysisRecord record, string id) =>
        record.Speakers.FirstOrDefault(x => x.SpeakerId == id)
        ?? throw new KeyNotFoundException($"話者が見つかりません: {id}");

    private static async Task RecalculateDurationsAsync(ProjectWorkspace workspace,
        SpeakerAnalysisRecord record, CancellationToken cancellationToken)
    {
        var durations = new Dictionary<string, double>(StringComparer.Ordinal);
        var directory = Path.Combine(workspace.MetadataPath, "preprocessing");
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preprocessing = await new AudioPreprocessingRepository().LoadAsync(workspace,
                    Path.GetFileNameWithoutExtension(path), cancellationToken);
                if (preprocessing is null) continue;
                foreach (var segment in preprocessing.Segments)
                {
                    var duration = segment.EndSec - segment.StartSec;
                    if (!double.IsFinite(duration) || duration < 0)
                        throw new InvalidDataException($"区間長が不正です: {segment.SegmentId}");
                    if (!durations.TryAdd(segment.SegmentId, duration))
                        throw new InvalidDataException($"前処理結果の区間IDが重複しています: {segment.SegmentId}");
                }
            }
        foreach (var assignment in record.Assignments)
            if (!durations.ContainsKey(assignment.SegmentId))
                throw new InvalidDataException($"区間長が見つかりません。前処理結果を確認してください: {assignment.SegmentId}");
        foreach (var speaker in record.Speakers)
        {
            var ids = record.Assignments.Where(x => x.SpeakerId == speaker.SpeakerId)
                .Select(x => x.SegmentId).Distinct(StringComparer.Ordinal).ToArray();
            speaker.TotalDurationSec = ids.Sum(id => durations[id]);
            speaker.UsableDurationSec = ids.Where(id => !record.ExcludedSegmentIds.Contains(id)).Sum(id => durations[id]);
        }
    }
}
