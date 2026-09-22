using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Speaker;

public sealed class SpeakerOverrideService(SpeakerAnalysisRepository repository)
{
    public async Task SetSelectedAsync(ProjectWorkspace workspace, string speakerId, bool selected,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        RequireSpeaker(record, speakerId).UserSelected = selected;
        await repository.SaveAsync(workspace, record, cancellationToken);
    }

    public async Task ExcludeSegmentAsync(ProjectWorkspace workspace, string segmentId, bool excluded,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        if (!record.Assignments.Any(x => x.SegmentId == segmentId))
            throw new KeyNotFoundException($"区間が見つかりません: {segmentId}");
        var durations = await LoadDurationsAsync(workspace, record, cancellationToken);
        if (excluded) record.ExcludedSegmentIds.Add(segmentId);
        else record.ExcludedSegmentIds.Remove(segmentId);
        RecalculateDurations(record, durations);
        await repository.SaveAsync(workspace, record, cancellationToken);
    }

    public async Task MergeSpeakersAsync(ProjectWorkspace workspace, string targetSpeakerId,
        IReadOnlyCollection<string> sourceSpeakerIds, CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        var target = RequireSpeaker(record, targetSpeakerId);
        var ids = sourceSpeakerIds.Where(x => x != targetSpeakerId).Distinct(StringComparer.Ordinal).ToArray();
        // 全IDを先に検証し、誤指定による部分的な統合を防ぐ。
        var sources = ids.Select(x => RequireSpeaker(record, x)).ToArray();
        if (sources.Length == 0) return;
        var durations = await LoadDurationsAsync(workspace, record, cancellationToken);
        RecalculateDurations(record, durations);
        var members = new[] { target }.Concat(sources).ToArray();
        var centroids = members.Where(x => x.EmbeddingCentroid.Count > 0).ToArray();
        if (centroids.Select(x => x.EmbeddingCentroid.Count).Distinct().Count() > 1)
            throw new InvalidDataException("統合対象の話者特徴の次元が一致しません。");
        var centroid = new double[centroids.FirstOrDefault()?.EmbeddingCentroid.Count ?? 0];
        foreach (var member in centroids)
            for (var i = 0; i < centroid.Length; i++)
                centroid[i] += member.EmbeddingCentroid[i] * member.TotalDurationSec;
        var norm = Math.Sqrt(centroid.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < centroid.Length; i++) centroid[i] /= norm;
        var representatives = members.SelectMany(x => x.RepresentativeSegmentIds)
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var assignment in record.Assignments.Where(x => ids.Contains(x.SpeakerId, StringComparer.Ordinal)))
            assignment.SpeakerId = targetSpeakerId;
        foreach (var source in sources)
        {
            target.MergedFrom.Add(source.SpeakerId);
            target.MergedFrom.AddRange(source.MergedFrom);
            target.UserSelected |= source.UserSelected;
            record.Speakers.Remove(source);
        }
        var mergedFrom = target.MergedFrom.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        target.MergedFrom.Clear(); target.MergedFrom.AddRange(mergedFrom);
        target.EmbeddingCentroid.Clear(); target.EmbeddingCentroid.AddRange(centroid);
        target.RepresentativeSegmentIds.Clear();
        target.RepresentativeSegmentIds.AddRange(representatives.Where(x => !record.ExcludedSegmentIds.Contains(x)).Take(3));
        record.MergeHistory.Add(new SpeakerMergeRecord { TargetSpeakerId = targetSpeakerId, SourceSpeakerIds = [.. ids] });
        RecalculateDurations(record, durations);
        await repository.SaveAsync(workspace, record, cancellationToken);
    }

    private async Task<SpeakerAnalysisRecord> RequireRecordAsync(ProjectWorkspace workspace, CancellationToken token) =>
        await repository.LoadAsync(workspace, token) ?? throw new InvalidOperationException("話者解析結果がありません。");

    private static SpeakerRecord RequireSpeaker(SpeakerAnalysisRecord record, string id) =>
        record.Speakers.FirstOrDefault(x => x.SpeakerId == id) ?? throw new KeyNotFoundException($"話者が見つかりません: {id}");

    private static async Task<Dictionary<string, double>> LoadDurationsAsync(ProjectWorkspace workspace,
        SpeakerAnalysisRecord record, CancellationToken token)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var directory = Path.Combine(workspace.MetadataPath, "preprocessing");
        var preprocessingRepository = new AudioPreprocessingRepository();
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var preprocessing = await preprocessingRepository.LoadAsync(workspace, Path.GetFileNameWithoutExtension(path), token);
                if (preprocessing is null || preprocessing.ErrorMessage is not null) continue;
                foreach (var segment in preprocessing.Segments)
                {
                    var duration = segment.EndSec - segment.StartSec;
                    if (!double.IsFinite(duration) || duration < 0)
                        throw new InvalidDataException($"区間長が不正です: {segment.SegmentId}");
                    if (!result.TryAdd(segment.SegmentId, duration) && result[segment.SegmentId] != duration)
                        throw new InvalidDataException($"区間長が重複して矛盾しています: {segment.SegmentId}");
                }
            }
        foreach (var assignment in record.Assignments)
            if (!result.ContainsKey(assignment.SegmentId))
                throw new InvalidOperationException($"使用時間の計算に必要な前処理結果がありません: {assignment.SegmentId}");
        return result;
    }

    private static void RecalculateDurations(SpeakerAnalysisRecord record, IReadOnlyDictionary<string, double> durations)
    {
        foreach (var speaker in record.Speakers)
        {
            var ids = record.Assignments.Where(x => x.SpeakerId == speaker.SpeakerId)
                .Select(x => x.SegmentId).Distinct(StringComparer.Ordinal).ToArray();
            speaker.TotalDurationSec = ids.Sum(x => durations[x]);
            speaker.UsableDurationSec = ids.Where(x => !record.ExcludedSegmentIds.Contains(x)).Sum(x => durations[x]);
        }
    }
}
