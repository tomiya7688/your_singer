using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Dataset;

public sealed class ContentClassificationService
{
    public const string StageVersion = "v1-04.1";

    private readonly MlWorkerClient _worker;
    private readonly AudioPreprocessingRepository _preprocessingRepository;
    private readonly ContentClassificationRepository _classificationRepository;

    public ContentClassificationService(
        MlWorkerClient worker,
        AudioPreprocessingRepository preprocessingRepository,
        ContentClassificationRepository classificationRepository)
    {
        _worker = worker;
        _preprocessingRepository = preprocessingRepository;
        _classificationRepository = classificationRepository;
    }

    public async Task<ContentClassificationRecord> ClassifyProjectAsync(
        ProjectWorkspace workspace,
        IEnumerable<SourceRecord> sources,
        CancellationToken cancellationToken = default)
    {
        var existing = await _classificationRepository.LoadAsync(
            workspace,
            cancellationToken);

        var segmentInputs = new List<object>();

        foreach (var source in sources)
        {
            var preprocessing = await _preprocessingRepository.LoadAsync(
                workspace,
                source.SourceId,
                cancellationToken);

            if (preprocessing is null || preprocessing.ErrorMessage is not null)
            {
                continue;
            }

            foreach (var segment in preprocessing.Segments)
            {
                segmentInputs.Add(new
                {
                    segment_id = segment.SegmentId,
                    audio_path = Path.Combine(workspace.RootPath, segment.AudioPath)
                });
            }
        }

        var result = await _worker.SendAsync(
            "classify_content",
            new
            {
                stage_version = StageVersion,
                segments = segmentInputs
            },
            cancellationToken);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        var record = JsonSerializer.Deserialize<ContentClassificationRecord>(
            result.GetRawText(),
            options) ?? throw new InvalidDataException(
                "Speech / Singing分類結果を読み取れませんでした。");

        if (existing is not null)
        {
            ApplyExistingOverrides(record, existing);
        }

        await _classificationRepository.SaveAsync(
            workspace,
            record,
            cancellationToken);

        return record;
    }

    private static void ApplyExistingOverrides(
        ContentClassificationRecord current,
        ContentClassificationRecord existing)
    {
        var currentById = current.Classifications.ToDictionary(x => x.SegmentId);
        var latestOverrideBySegment = existing.Overrides
            .GroupBy(x => x.SegmentId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(x => x.ChangedAt).First());

        foreach (var (segmentId, userOverride) in latestOverrideBySegment)
        {
            if (!currentById.TryGetValue(segmentId, out var classification))
            {
                continue;
            }

            classification.ContentType = userOverride.NewType;
            classification.UserOverridden = true;
            current.Overrides.Add(userOverride);
        }
    }
}
