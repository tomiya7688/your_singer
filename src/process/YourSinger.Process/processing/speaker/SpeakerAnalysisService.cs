using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Speaker;

public sealed class SpeakerAnalysisService
{
    public const string StageVersion = "v1-03.1";

    private readonly MlWorkerClient _worker;
    private readonly AudioPreprocessingRepository _preprocessingRepository;
    private readonly SpeakerAnalysisRepository _speakerRepository;

    public SpeakerAnalysisService(
        MlWorkerClient worker,
        AudioPreprocessingRepository preprocessingRepository,
        SpeakerAnalysisRepository speakerRepository)
    {
        _worker = worker;
        _preprocessingRepository = preprocessingRepository;
        _speakerRepository = speakerRepository;
    }

    public async Task<SpeakerAnalysisRecord> AnalyzeProjectAsync(
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
                    audio_path = Path.Combine(workspace.RootPath, segment.AudioPath),
                    duration_sec = segment.EndSec - segment.StartSec
                });
            }
        }

        var result = await _worker.SendAsync(
            "analyze_speakers",
            new
            {
                stage_version = StageVersion,
                segments = segmentInputs
            },
            cancellationToken);

        var record = JsonSerializer.Deserialize<SpeakerAnalysisRecord>(
            result.GetRawText(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }) ?? throw new InvalidDataException("話者解析結果を読み取れませんでした。");

        await _speakerRepository.SaveAsync(workspace, record, cancellationToken);
        return record;
    }
}
