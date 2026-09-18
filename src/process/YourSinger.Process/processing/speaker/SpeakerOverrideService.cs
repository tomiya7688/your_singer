using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Speaker;

public sealed class SpeakerOverrideService
{
    private readonly SpeakerAnalysisRepository _repository;

    public SpeakerOverrideService(SpeakerAnalysisRepository repository)
    {
        _repository = repository;
    }

    public async Task SetSelectedAsync(
        ProjectWorkspace workspace,
        string speakerId,
        bool selected,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        var speaker = RequireSpeaker(record, speakerId);
        speaker.UserSelected = selected;
        await _repository.SaveAsync(workspace, record, cancellationToken);
    }

    public async Task ExcludeSegmentAsync(
        ProjectWorkspace workspace,
        string segmentId,
        bool excluded,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);

        if (excluded)
        {
            record.ExcludedSegmentIds.Add(segmentId);
        }
        else
        {
            record.ExcludedSegmentIds.Remove(segmentId);
        }

        RecalculateDurations(record);
        await _repository.SaveAsync(workspace, record, cancellationToken);
    }

    public async Task MergeSpeakersAsync(
        ProjectWorkspace workspace,
        string targetSpeakerId,
        IReadOnlyCollection<string> sourceSpeakerIds,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        var target = RequireSpeaker(record, targetSpeakerId);
        var mergeIds = sourceSpeakerIds
            .Where(id => !string.Equals(id, targetSpeakerId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var sourceId in mergeIds)
        {
            var source = RequireSpeaker(record, sourceId);

            foreach (var assignment in record.Assignments.Where(x => x.SpeakerId == sourceId))
            {
                assignment.SpeakerId = targetSpeakerId;
            }

            target.MergedFrom.Add(sourceId);
            target.MergedFrom.AddRange(source.MergedFrom);
            record.Speakers.Remove(source);
        }

        target.MergedFrom.Sort(StringComparer.Ordinal);
        for (var index = target.MergedFrom.Count - 1; index > 0; index--)
        {
            if (target.MergedFrom[index] == target.MergedFrom[index - 1])
            {
                target.MergedFrom.RemoveAt(index);
            }
        }

        record.MergeHistory.Add(new SpeakerMergeRecord
        {
            TargetSpeakerId = targetSpeakerId,
            SourceSpeakerIds = mergeIds.ToList()
        });

        RecalculateDurations(record);
        await _repository.SaveAsync(workspace, record, cancellationToken);
    }

    private async Task<SpeakerAnalysisRecord> RequireRecordAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken)
    {
        return await _repository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("話者解析結果がありません。");
    }

    private static SpeakerRecord RequireSpeaker(SpeakerAnalysisRecord record, string speakerId) =>
        record.Speakers.FirstOrDefault(x => x.SpeakerId == speakerId)
        ?? throw new KeyNotFoundException($"話者が見つかりません: {speakerId}");

    private static void RecalculateDurations(SpeakerAnalysisRecord record)
    {
        foreach (var speaker in record.Speakers)
        {
            var assigned = record.Assignments
                .Where(x => x.SpeakerId == speaker.SpeakerId)
                .Select(x => x.SegmentId)
                .ToHashSet(StringComparer.Ordinal);

            speaker.UsableDurationSec = speaker.TotalDurationSec *
                (assigned.Count == 0
                    ? 0
                    : (double)assigned.Count(x => !record.ExcludedSegmentIds.Contains(x)) / assigned.Count);
        }
    }
}
