using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Dataset;

public sealed class ContentDatasetRoutingService
{
    private readonly ContentClassificationRepository _repository;

    public ContentDatasetRoutingService(ContentClassificationRepository repository)
    {
        _repository = repository;
    }

    public async Task SetOverrideAsync(
        ProjectWorkspace workspace,
        string segmentId,
        SegmentContentType? overrideType,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        var segment = RequireSegment(record, segmentId);

        segment.UserOverrideType = overrideType;
        ApplyRouting(segment);

        await _repository.SaveAsync(workspace, record, cancellationToken);
    }

    public async Task SetAmbiguousRoutingAsync(
        ProjectWorkspace workspace,
        string segmentId,
        bool includeInTalkDataset,
        bool includeInSingingDataset,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        var segment = RequireSegment(record, segmentId);

        if (segment.EffectiveType != SegmentContentType.Ambiguous)
        {
            throw new InvalidOperationException(
                "曖昧区間以外には個別の両用途振り分けを設定できません。");
        }

        segment.IncludeInTalkDataset = includeInTalkDataset;
        segment.IncludeInSingingDataset = includeInSingingDataset;

        await _repository.SaveAsync(workspace, record, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetTalkSegmentIdsAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        return record.Segments
            .Where(x => x.IncludeInTalkDataset)
            .Select(x => x.SegmentId)
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetSingingSegmentIdsAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        var record = await RequireRecordAsync(workspace, cancellationToken);
        return record.Segments
            .Where(x => x.IncludeInSingingDataset)
            .Select(x => x.SegmentId)
            .ToArray();
    }

    public static void ApplyRouting(SegmentContentClassification segment)
    {
        switch (segment.EffectiveType)
        {
            case SegmentContentType.Speech:
                segment.IncludeInTalkDataset = true;
                segment.IncludeInSingingDataset = false;
                break;
            case SegmentContentType.Singing:
                segment.IncludeInTalkDataset = false;
                segment.IncludeInSingingDataset = true;
                break;
            case SegmentContentType.Ambiguous:
                segment.IncludeInTalkDataset = true;
                segment.IncludeInSingingDataset = true;
                break;
        }
    }

    private async Task<ContentClassificationRecord> RequireRecordAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken)
    {
        return await _repository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("Speech / Singing分類結果がありません。");
    }

    private static SegmentContentClassification RequireSegment(
        ContentClassificationRecord record,
        string segmentId) =>
        record.Segments.FirstOrDefault(x => x.SegmentId == segmentId)
        ?? throw new KeyNotFoundException($"セグメントが見つかりません: {segmentId}");
}
