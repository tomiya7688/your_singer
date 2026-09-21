using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Processing.Training;

public sealed class TrainingJobPlanningService
{
    public const string StageVersion = "v1-09.1";

    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    private readonly AutoCorrectionService _autoCorrectionService;
    private readonly TrainingJobRepository _jobRepository;

    public TrainingJobPlanningService(
        UniversalVoiceDatasetRepository datasetRepository,
        AutoCorrectionService autoCorrectionService,
        TrainingJobRepository jobRepository)
    {
        _datasetRepository = datasetRepository;
        _autoCorrectionService = autoCorrectionService;
        _jobRepository = jobRepository;
    }

    public async Task<TrainingJobBatch> CreateBatchAsync(
        ProjectWorkspace workspace,
        IReadOnlyList<SpeakerTrainingSelection> selections,
        CancellationToken cancellationToken = default)
    {
        if (selections.Count == 0)
            throw new ArgumentException("学習対象話者が選択されていません。", nameof(selections));

        var dataset = await _datasetRepository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("Universal Voice Datasetがありません。");

        var batch = new TrainingJobBatch
        {
            BatchId = $"batch-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}"
        };

        foreach (var selection in selections)
        {
            var corrected = await _autoCorrectionService.BuildAsync(
                workspace,
                selection.AutoCorrectionEnabled,
                cancellationToken);

            foreach (var target in ExpandTargets(selection.Target))
            {
                var segments = corrected.Segments
                    .Where(x => x.SpeakerId == selection.SpeakerId)
                    .Where(x => MatchesTarget(x, target))
                    .ToArray();

                var job = new TrainingJobRecord
                {
                    JobId = $"{selection.SpeakerId}-{target.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
                    SpeakerId = selection.SpeakerId,
                    Target = target,
                    AutoCorrectionEnabled = selection.AutoCorrectionEnabled,
                    DatasetFingerprint = corrected.Fingerprint,
                    SegmentIds = segments.Select(x => x.SegmentId).ToList()
                };

                batch.Jobs.Add(job);
            }
        }

        await _jobRepository.SaveBatchAsync(workspace, batch, cancellationToken);
        return batch;
    }

    public async Task<TrainingJobBatch> RecreateBatchAsync(
        ProjectWorkspace workspace,
        TrainingJobBatch sourceBatch,
        CancellationToken cancellationToken = default)
    {
        var selections = sourceBatch.Jobs
            .GroupBy(x => new { x.SpeakerId, x.AutoCorrectionEnabled })
            .Select(group => new SpeakerTrainingSelection
            {
                SpeakerId = group.Key.SpeakerId,
                AutoCorrectionEnabled = group.Key.AutoCorrectionEnabled,
                Target = group.Any(x => x.Target == TrainingTarget.Talk) &&
                         group.Any(x => x.Target == TrainingTarget.Singing)
                    ? TrainingTarget.Both
                    : group.First().Target
            })
            .ToArray();

        return await CreateBatchAsync(workspace, selections, cancellationToken);
    }

    public async Task UpdateStateAsync(
        ProjectWorkspace workspace,
        string batchId,
        string jobId,
        TrainingJobState state,
        IReadOnlyList<TrainingArtifactRecord>? artifacts = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        var batch = await _jobRepository.LoadBatchAsync(workspace, batchId, cancellationToken)
            ?? throw new InvalidOperationException("学習ジョブbatchがありません。");

        var job = batch.Jobs.FirstOrDefault(x => x.JobId == jobId)
            ?? throw new InvalidOperationException("学習ジョブがありません。");

        job.State = state;
        job.ErrorMessage = errorMessage;

        if (state == TrainingJobState.Running && job.StartedAt is null)
            job.StartedAt = DateTimeOffset.UtcNow;

        if (state is TrainingJobState.Succeeded or TrainingJobState.Failed or TrainingJobState.Canceled)
            job.CompletedAt = DateTimeOffset.UtcNow;

        if (artifacts is not null)
        {
            job.Artifacts.Clear();
            job.Artifacts.AddRange(artifacts);
        }

        await _jobRepository.SaveBatchAsync(workspace, batch, cancellationToken);
    }

    private static IEnumerable<TrainingTarget> ExpandTargets(TrainingTarget target)
    {
        if (target.HasFlag(TrainingTarget.Talk))
            yield return TrainingTarget.Talk;
        if (target.HasFlag(TrainingTarget.Singing))
            yield return TrainingTarget.Singing;
    }

    private static bool MatchesTarget(UniversalVoiceSegment segment, TrainingTarget target) =>
        target switch
        {
            TrainingTarget.Talk => segment.ContentType == SegmentContentType.Speech,
            TrainingTarget.Singing => segment.ContentType == SegmentContentType.Singing,
            _ => false
        };
}
