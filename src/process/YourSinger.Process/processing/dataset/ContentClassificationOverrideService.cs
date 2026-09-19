using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Dataset;

public sealed class ContentClassificationOverrideService
{
    private readonly ContentClassificationRepository _repository;

    public ContentClassificationOverrideService(
        ContentClassificationRepository repository)
    {
        _repository = repository;
    }

    public async Task SetContentTypeAsync(
        ProjectWorkspace workspace,
        string segmentId,
        SegmentContentType contentType,
        CancellationToken cancellationToken = default)
    {
        var record = await _repository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException(
                "Speech / Singing分類結果がありません。");

        var item = record.Classifications.FirstOrDefault(x => x.SegmentId == segmentId)
            ?? throw new KeyNotFoundException(
                $"分類対象セグメントが見つかりません: {segmentId}");

        if (item.ContentType == contentType && item.UserOverridden)
        {
            return;
        }

        record.Overrides.Add(new ContentClassificationOverride
        {
            SegmentId = segmentId,
            OriginalType = item.ContentType,
            NewType = contentType
        });

        item.ContentType = contentType;
        item.UserOverridden = true;

        await _repository.SaveAsync(
            workspace,
            record,
            cancellationToken);
    }
}
