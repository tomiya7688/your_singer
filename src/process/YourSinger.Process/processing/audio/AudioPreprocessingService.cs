using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Audio;

public sealed class AudioPreprocessingService
{
    public const string StageVersion = "v1-02.1";

    private readonly MlWorkerClient _worker;
    private readonly AudioPreprocessingRepository _repository;

    public AudioPreprocessingService(
        MlWorkerClient worker,
        AudioPreprocessingRepository repository)
    {
        _worker = worker;
        _repository = repository;
    }

    public async Task<AudioPreprocessingRecord> ProcessAsync(
        ProjectWorkspace workspace,
        SourceRecord source,
        IProgress<AudioPreprocessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (source.ExtractedAudioPath is null)
        {
            throw new InvalidOperationException(
                "抽出済み音声がありません。先にv1-01の入力処理を実行してください。");
        }

        var cached = await _repository.LoadAsync(
            workspace,
            source.SourceId,
            cancellationToken);

        if (cached is not null &&
            cached.StageVersion == StageVersion &&
            cached.ErrorMessage is null &&
            cached.CleanedAudioPath is not null &&
            File.Exists(Path.Combine(workspace.RootPath, cached.CleanedAudioPath)))
        {
            progress?.Report(new(
                source.SourceId,
                "cache",
                1,
                "前処理済み成果物を再利用しました"));

            return cached;
        }

        progress?.Report(new(
            source.SourceId,
            "preprocessing",
            0.05,
            "音声前処理を開始します"));

        var inputPath = Path.Combine(workspace.RootPath, source.ExtractedAudioPath);

        try
        {
            var result = await _worker.SendAsync(
                "preprocess_audio",
                new
                {
                    source_id = source.SourceId,
                    input_path = inputPath,
                    workspace_path = workspace.RootPath,
                    stage_version = StageVersion
                },
                cancellationToken);

            var record = ParseResult(result);
            await _repository.SaveAsync(workspace, record, cancellationToken);

            progress?.Report(new(
                source.SourceId,
                "completed",
                1,
                "音声前処理が完了しました"));

            return record;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = new AudioPreprocessingRecord
            {
                SourceId = source.SourceId,
                StageVersion = StageVersion,
                RawAudioPath = source.ExtractedAudioPath,
                ErrorMessage = exception.Message
            };

            await _repository.SaveAsync(workspace, failed, cancellationToken);
            throw;
        }
    }

    private static AudioPreprocessingRecord ParseResult(JsonElement result)
    {
        var record = JsonSerializer.Deserialize<AudioPreprocessingRecord>(
            result.GetRawText(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

        return record ?? throw new InvalidDataException(
            "ML workerの前処理結果を読み取れませんでした。");
    }
}
