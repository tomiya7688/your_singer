using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Audio;

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

    public async Task<ContentClassificationRecord> AnalyzeProjectAsync(
        ProjectWorkspace workspace,
        IEnumerable<SourceRecord> sources,
        CancellationToken cancellationToken = default)
    {
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

        var record = JsonSerializer.Deserialize<ContentClassificationRecord>(
            result.GetRawText(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }) ?? throw new InvalidDataException("Speech / Singing分類結果を読み取れませんでした。");

        foreach (var segment in record.Segments)
        {
            ContentDatasetRoutingService.ApplyRouting(segment);
        }

        await _classificationRepository.SaveAsync(
            workspace,
            record,
            cancellationToken);

        return record;
    }
}
