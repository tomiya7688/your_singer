using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Dataset;

public sealed class UniversalFeatureOverrideService
{
    private readonly UniversalVoiceDatasetRepository _repository;

    public UniversalFeatureOverrideService(
        UniversalVoiceDatasetRepository repository)
    {
        _repository = repository;
    }

    public async Task SetTranscriptAsync(
        ProjectWorkspace workspace,
        string segmentId,
        string transcript,
        CancellationToken cancellationToken = default)
    {
        var dataset = await _repository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException(
                "Universal Voice Datasetがありません。");

        var segment = dataset.Segments.FirstOrDefault(x => x.SegmentId == segmentId)
            ?? throw new KeyNotFoundException(
                $"対象セグメントが見つかりません: {segmentId}");

        segment.Transcript = transcript;
        dataset.Overrides.Add(new UserFeatureOverrideRecord
        {
            SegmentId = segmentId,
            Field = "transcript",
            Value = transcript
        });

        await _repository.SaveAsync(workspace, dataset, cancellationToken);
    }
}
