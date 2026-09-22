using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Processing.Training;

public sealed class TrainingJobPlanningService
{
    public const string StageVersion = "v1-09.3";
    public const string CurrentInputVersion = StageVersion + "/" + AutoCorrectionService.StageVersion;
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

    public static bool RequiresRecreation(TrainingJobBatch batch) =>
        !string.Equals(batch.InputVersion, CurrentInputVersion, StringComparison.Ordinal);

    public Task<TrainingJobBatch> CreateBatchAsync(ProjectWorkspace workspace,
        IReadOnlyList<SpeakerTrainingSelection> selections, CancellationToken cancellationToken = default) =>
        CreateBatchCoreAsync(workspace, selections, null, cancellationToken);

    private async Task<TrainingJobBatch> CreateBatchCoreAsync(ProjectWorkspace workspace,
        IReadOnlyList<SpeakerTrainingSelection> selections, string? recreatedFromBatchId,
        CancellationToken cancellationToken)
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
        var batch = new TrainingJobBatch
        {
            BatchId = $"batch-{Guid.NewGuid():N}", InputVersion = CurrentInputVersion,
            RecreatedFromBatchId = recreatedFromBatchId
        };
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
                    throw new InvalidOperationException($"話者 {selection.SpeakerId} の{(target == TrainingTarget.Talk ? "トーク" : "歌唱")}用素材がありません。話者・用途・除外設定を確認してください。");
                batch.Jobs.Add(new TrainingJobRecord
                {
                    JobId = $"job-{Guid.NewGuid():N}", SpeakerId = selection.SpeakerId, Target = target,
                    AutoCorrectionEnabled = selection.AutoCorrectionEnabled,
                    DatasetFingerprint = TrainingDatasetSnapshotService.CreateJobFingerprint(view.Fingerprint, selection.SpeakerId, target, ids),
                    SegmentIds = ids
                });
            }
        }
        // 新しいバッチだけを公開し、同じIDの既存ファイルは上書きしない。
        await _jobRepository.CreateBatchAsync(workspace, batch, cancellationToken);
        return batch;
    }

    public Task<TrainingJobBatch> RecreateBatchAsync(ProjectWorkspace workspace, TrainingJobBatch sourceBatch,
        CancellationToken cancellationToken = default) =>
        RecreateBatchAsync(workspace, sourceBatch.BatchId, cancellationToken);

    public async Task<TrainingJobBatch> RecreateBatchAsync(ProjectWorkspace workspace, string sourceBatchId,
        CancellationToken cancellationToken = default)
    {
        // 画面表示時の古い状態ではなく、確認後に保存済みの設定と状態を読み直す。
        var source = await _jobRepository.LoadBatchAsync(workspace, sourceBatchId, cancellationToken)
            ?? throw new InvalidOperationException("再作成元の学習ジョブが見つかりません。一覧を更新してください。");
        if (source.Jobs.Any(job => job.State == TrainingJobState.Running))
            throw new InvalidOperationException("実行中のジョブを含むため再作成できません。処理の終了または中止を確認してください。");
        var selections = source.Jobs.GroupBy(x => new { x.SpeakerId, x.AutoCorrectionEnabled })
            .Select(group => new SpeakerTrainingSelection
            {
                SpeakerId = group.Key.SpeakerId, AutoCorrectionEnabled = group.Key.AutoCorrectionEnabled,
                Target = group.Aggregate(TrainingTarget.None, (target, job) => target | job.Target)
            }).ToArray();
        if (selections.Select(x => x.SpeakerId).Distinct(StringComparer.Ordinal).Count() != selections.Length)
            throw new InvalidOperationException("同じ話者に異なる補完設定が混在しています。話者・用途を選び直して新しいジョブを作成してください。");
        return await CreateBatchCoreAsync(workspace, selections, source.BatchId, cancellationToken);
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
