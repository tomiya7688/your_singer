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
        ArgumentNullException.ThrowIfNull(selections);
        cancellationToken.ThrowIfCancellationRequested();
        if (selections.Count == 0)
            throw new ArgumentException("学習対象話者が選択されていません。", nameof(selections));
        foreach (var selection in selections)
            if (string.IsNullOrWhiteSpace(selection.SpeakerId) ||
                selection.Target is not (TrainingTarget.Talk or TrainingTarget.Singing or TrainingTarget.Both))
                throw new ArgumentException("話者または学習用途の指定が不正です。", nameof(selections));
        if (selections.Select(x => x.SpeakerId).Distinct(StringComparer.Ordinal).Count() != selections.Count)
            throw new ArgumentException("同じ話者が重複して選択されています。", nameof(selections));

        _ = await _datasetRepository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("共通音声データがありません。");
        var batch = new TrainingJobBatch { BatchId = $"batch-{Guid.NewGuid():N}" };
        var views = new Dictionary<bool, CorrectedDatasetView>();
        foreach (var selection in selections)
        {
            if (!views.TryGetValue(selection.AutoCorrectionEnabled, out var view))
            {
                view = await _autoCorrectionService.BuildAsync(workspace, selection.AutoCorrectionEnabled, cancellationToken);
                views.Add(selection.AutoCorrectionEnabled, view);
            }
            foreach (var target in ExpandTargets(selection.Target))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var type = target == TrainingTarget.Talk ? SegmentContentType.Speech : SegmentContentType.Singing;
                var ids = view.Segments.Where(x => x.SpeakerId == selection.SpeakerId && x.ContentType == type)
                    .Select(x => x.SegmentId).Order(StringComparer.Ordinal).ToList();
                if (ids.Count == 0)
                    throw new InvalidOperationException($"話者 {selection.SpeakerId} の{(target == TrainingTarget.Talk ? "トーク" : "歌唱")}用素材がありません。");
                batch.Jobs.Add(new TrainingJobRecord
                {
                    JobId = $"job-{Guid.NewGuid():N}", SpeakerId = selection.SpeakerId, Target = target,
                    AutoCorrectionEnabled = selection.AutoCorrectionEnabled,
                    DatasetFingerprint = TrainingDatasetSnapshotService.CreateJobFingerprint(view.Fingerprint, selection.SpeakerId, target, ids),
                    SegmentIds = ids
                });
            }
        }
        // 全選択の検証が終わるまでは途中のバッチを保存しない。
        await _jobRepository.SaveBatchAsync(workspace, batch, cancellationToken);
        return batch;
    }

    public Task<TrainingJobBatch> RecreateBatchAsync(ProjectWorkspace workspace, TrainingJobBatch sourceBatch,
        CancellationToken cancellationToken = default)
    {
        var selections = sourceBatch.Jobs.GroupBy(x => new { x.SpeakerId, x.AutoCorrectionEnabled })
            .Select(group => new SpeakerTrainingSelection
            {
                SpeakerId = group.Key.SpeakerId, AutoCorrectionEnabled = group.Key.AutoCorrectionEnabled,
                Target = group.Aggregate(TrainingTarget.None, (target, job) => target | job.Target)
            }).ToArray();
        return CreateBatchAsync(workspace, selections, cancellationToken);
    }

    public async Task UpdateStateAsync(ProjectWorkspace workspace, string batchId, string jobId,
        TrainingJobState state, IReadOnlyList<TrainingArtifactRecord>? artifacts = null,
        string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state), "学習状態が不正です。");
        var batch = await _jobRepository.LoadBatchAsync(workspace, batchId, cancellationToken)
            ?? throw new InvalidOperationException("学習ジョブのバッチがありません。");
        var job = batch.Jobs.FirstOrDefault(x => x.JobId == jobId)
            ?? throw new InvalidOperationException("学習ジョブがありません。");
        if (job.State != state && !CanTransition(job.State, state))
            throw new InvalidOperationException("許可されていない学習状態の変更です。再学習には新しいジョブを作成してください。");
        job.State = state;
        job.ErrorMessage = errorMessage;
        if (state == TrainingJobState.Running) job.StartedAt ??= DateTimeOffset.UtcNow;
        if (state is TrainingJobState.Succeeded or TrainingJobState.Failed or TrainingJobState.Canceled)
            job.CompletedAt ??= DateTimeOffset.UtcNow;
        if (artifacts is not null)
        {
            job.Artifacts.Clear();
            job.Artifacts.AddRange(artifacts);
        }
        await _jobRepository.SaveBatchAsync(workspace, batch, cancellationToken);
    }

    private static bool CanTransition(TrainingJobState from, TrainingJobState to) => from switch
    {
        TrainingJobState.Pending => to is TrainingJobState.Running or TrainingJobState.Canceled,
        TrainingJobState.Running => to is TrainingJobState.Succeeded or TrainingJobState.Failed or TrainingJobState.Canceled,
        _ => false
    };

    private static IEnumerable<TrainingTarget> ExpandTargets(TrainingTarget target)
    {
        if (target.HasFlag(TrainingTarget.Talk)) yield return TrainingTarget.Talk;
        if (target.HasFlag(TrainingTarget.Singing)) yield return TrainingTarget.Singing;
    }
}
