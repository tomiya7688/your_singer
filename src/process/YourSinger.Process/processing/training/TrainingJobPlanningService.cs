using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Processing.Training;

public sealed class TrainingJobPlanningService
{
    public const string StageVersion = "v1-09.2";
    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    private readonly AutoCorrectionService _autoCorrectionService;
    private readonly TrainingJobRepository _jobRepository;

    public TrainingJobPlanningService(UniversalVoiceDatasetRepository datasetRepository,
        AutoCorrectionService autoCorrectionService, TrainingJobRepository jobRepository)
    {
        _datasetRepository = datasetRepository;
        _autoCorrectionService = autoCorrectionService;
        _jobRepository = jobRepository;
    }

    public async Task<TrainingJobBatch> CreateBatchAsync(ProjectWorkspace workspace,
        IReadOnlyList<SpeakerTrainingSelection> selections, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selections.Count == 0)
            throw new ArgumentException("学習対象話者が選択されていません。", nameof(selections));
        if (selections.Any(x => string.IsNullOrWhiteSpace(x.SpeakerId) || x.Target == TrainingTarget.None ||
            (x.Target & ~TrainingTarget.Both) != 0))
            throw new ArgumentException("話者と学習用途を正しく指定してください。", nameof(selections));
        if (selections.Select(x => x.SpeakerId).Distinct(StringComparer.Ordinal).Count() != selections.Count)
            throw new ArgumentException("同じ話者を重複して選択できません。", nameof(selections));
        _ = await _datasetRepository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("共通音声データセットがありません。");

        var batch = new TrainingJobBatch { BatchId = $"batch-{Guid.NewGuid():N}" };
        var views = new Dictionary<bool, CorrectedDatasetView>();
        foreach (var selection in selections)
        {
            if (!views.TryGetValue(selection.AutoCorrectionEnabled, out var corrected))
            {
                corrected = await _autoCorrectionService.BuildAsync(workspace,
                    selection.AutoCorrectionEnabled, cancellationToken);
                views.Add(selection.AutoCorrectionEnabled, corrected);
            }
            foreach (var target in ExpandTargets(selection.Target))
            {
                var ids = corrected.Segments.Where(x => x.SpeakerId == selection.SpeakerId && MatchesTarget(x, target))
                    .Select(x => x.SegmentId).Order(StringComparer.Ordinal).ToList();
                if (ids.Count == 0)
                    throw new InvalidOperationException($"話者 {selection.SpeakerId} の{(target == TrainingTarget.Talk ? "トーク" : "歌唱")}学習に使用できる区間がありません。");
                batch.Jobs.Add(new()
                {
                    JobId = $"job-{Guid.NewGuid():N}", SpeakerId = selection.SpeakerId, Target = target,
                    AutoCorrectionEnabled = selection.AutoCorrectionEnabled,
                    DatasetFingerprint = DatasetFingerprint.ForJob(corrected.Fingerprint, selection.SpeakerId, target, ids),
                    SegmentIds = ids
                });
            }
        }
        await _jobRepository.SaveBatchAsync(workspace, batch, cancellationToken);
        return batch;
    }

    public async Task<TrainingJobBatch> RecreateBatchAsync(ProjectWorkspace workspace,
        TrainingJobBatch sourceBatch, CancellationToken cancellationToken = default)
    {
        var selections = sourceBatch.Jobs.GroupBy(x => new { x.SpeakerId, x.AutoCorrectionEnabled })
            .Select(group => new SpeakerTrainingSelection
            {
                SpeakerId = group.Key.SpeakerId, AutoCorrectionEnabled = group.Key.AutoCorrectionEnabled,
                Target = group.Aggregate(TrainingTarget.None, (target, job) => target | job.Target)
            }).ToArray();
        return await CreateBatchAsync(workspace, selections, cancellationToken);
    }

    public async Task UpdateStateAsync(ProjectWorkspace workspace, string batchId, string jobId,
        TrainingJobState state, IReadOnlyList<TrainingArtifactRecord>? artifacts = null,
        string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        var batch = await _jobRepository.LoadBatchAsync(workspace, batchId, cancellationToken)
            ?? throw new InvalidOperationException("学習ジョブの一覧がありません。");
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
        if (target.HasFlag(TrainingTarget.Talk)) yield return TrainingTarget.Talk;
        if (target.HasFlag(TrainingTarget.Singing)) yield return TrainingTarget.Singing;
    }

    internal static bool MatchesTarget(UniversalVoiceSegment segment, TrainingTarget target) => target switch
    {
        TrainingTarget.Talk => segment.ContentType == SegmentContentType.Speech,
        TrainingTarget.Singing => segment.ContentType == SegmentContentType.Singing,
        _ => false
    };
}
