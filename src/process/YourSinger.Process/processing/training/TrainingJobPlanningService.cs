using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [TrainingJobPlanningService: 学習対象を検証し学習ジョブのバッチを作成・更新する]
//   フィールド: [datasetRepository: 共通音声データ, autoCorrectionService: 補完・補正後のデータ, jobRepository: 学習ジョブの永続化]
//   処理: [1: 選択内容と歌唱alignmentを検証してバッチを作成する]
// }
public sealed class TrainingJobPlanningService
{
    public const string StageVersion = "v1-09.4";
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

    // {
    //   責務: [CreateBatchCoreAsync: 選択された話者・用途から学習ジョブを作成する]
    //   処理: [
    //     1: 選択内容と共通音声データを検証する
    //     2: 補完・補正済みデータから対象segmentを選ぶ
    //     3: 歌唱用segmentの音素alignmentを検証する
    //     4: バッチを新規保存する
    //   ]
    //   引数: [workspace: 作業領域, selections: 話者と学習用途の選択, recreatedFromBatchId: 再作成元のID]
    //   戻り値: [batch: 保存した学習ジョブバッチ]
    //   エラー: [選択または入力データが不正な場合に例外を送出する]
    // }
    private async Task<TrainingJobBatch> CreateBatchCoreAsync(ProjectWorkspace workspace,
        IReadOnlyList<SpeakerTrainingSelection> selections, string? recreatedFromBatchId,
        CancellationToken cancellationToken)
    {
        // 選択検証を先に完了し、部分的なjobを保存しない。
        ValidateSelections(selections);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await _datasetRepository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("共通音声データがありません。");
        var batch = new TrainingJobBatch
        {
            BatchId = $"batch-{Guid.NewGuid():N}", InputVersion = CurrentInputVersion,
            RecreatedFromBatchId = recreatedFromBatchId
        };
        var views = new Dictionary<bool, CorrectedDatasetView>();
        foreach (var selection in selections)
            await AppendSelectionJobsAsync(workspace, selection, batch, views, cancellationToken);
        // 新しいバッチだけを公開し、同じIDの既存ファイルは上書きしない。
        await _jobRepository.CreateBatchAsync(workspace, batch, cancellationToken);
        return batch;
    }

    // {
    //   責務: [ValidateSelections: 学習対象の選択内容を検証する]
    //   処理: [1: 話者ID、用途、重複選択を確認する]
    //   引数: [selections: 話者と学習用途の選択]
    //   エラー: [不正な選択がある場合にArgumentExceptionを送出する]
    // }
    private static void ValidateSelections(IReadOnlyList<SpeakerTrainingSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count == 0)
            throw new ArgumentException("学習対象話者が選択されていません。", nameof(selections));
        foreach (var selection in selections)
            if (string.IsNullOrWhiteSpace(selection.SpeakerId) ||
                selection.Target is not (TrainingTarget.Talk or TrainingTarget.Singing or TrainingTarget.Both))
                throw new ArgumentException("話者または学習用途の指定が不正です。", nameof(selections));
        if (selections.Select(x => x.SpeakerId).Distinct(StringComparer.Ordinal).Count() != selections.Count)
            throw new ArgumentException("同じ話者が重複して選択されています。", nameof(selections));
    }

    // {
    //   責務: [AppendSelectionJobsAsync: 選択ひとつに対応する学習jobをバッチへ追加する]
    //   処理: [
    //     1: 補完・補正設定に対応するviewを再利用または作成する
    //     2: 選択用途ごとのjobを作成する
    //   ]
    //   引数: [workspace: 作業領域, selection: 話者と用途の選択, batch: job追加先, views: 設定別view cache]
    // }
    private async Task AppendSelectionJobsAsync(ProjectWorkspace workspace, SpeakerTrainingSelection selection,
        TrainingJobBatch batch, IDictionary<bool, CorrectedDatasetView> views, CancellationToken cancellationToken)
    {
        // 同じ補完・補正設定のviewはバッチ内で一度だけ作成する。
        if (!views.TryGetValue(selection.AutoCorrectionEnabled, out var view))
        {
            view = await _autoCorrectionService.BuildAsync(workspace, selection.AutoCorrectionEnabled, cancellationToken);
            views.Add(selection.AutoCorrectionEnabled, view);
        }
        foreach (var target in ExpandTargets(selection.Target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddTargetJob(batch, selection, target, view);
        }
    }

    // {
    //   責務: [AddTargetJob: 用途別segmentを選び、検証済みjobをバッチに追加する]
    //   処理: [1: 対象segmentを抽出し、歌唱用途ならforced alignmentを検証する]
    //   引数: [batch: job追加先, selection: 話者設定, target: 学習用途, view: 補完・補正済みデータ]
    //   エラー: [対象素材がない場合またはalignment不正時にInvalidOperationExceptionを送出する]
    // }
    private static void AddTargetJob(TrainingJobBatch batch, SpeakerTrainingSelection selection,
        TrainingTarget target, CorrectedDatasetView view)
    {
        var contentType = target == TrainingTarget.Talk ? SegmentContentType.Speech : SegmentContentType.Singing;
        var segmentIds = view.Segments.Where(segment => segment.SpeakerId == selection.SpeakerId && segment.ContentType == contentType)
            .Select(segment => segment.SegmentId).Order(StringComparer.Ordinal).ToList();
        if (segmentIds.Count == 0)
            throw new InvalidOperationException($"話者 {selection.SpeakerId} の{(target == TrainingTarget.Talk ? "トーク" : "歌唱")}用素材がありません。話者・用途・除外設定を確認してください。");
        if (target == TrainingTarget.Singing)
            EnsureSingingAlignment(view.Segments, segmentIds);

        batch.Jobs.Add(new TrainingJobRecord
        {
            JobId = $"job-{Guid.NewGuid():N}", SpeakerId = selection.SpeakerId, Target = target,
            AutoCorrectionEnabled = selection.AutoCorrectionEnabled,
            DatasetFingerprint = TrainingDatasetSnapshotService.CreateJobFingerprint(view.Fingerprint, selection.SpeakerId, target, segmentIds),
            SegmentIds = segmentIds
        });
    }

    // {
    //   責務: [EnsureSingingAlignment: 歌唱学習対象の全segmentに検証済みalignmentがあることを確認する]
    //   処理: [1: 対象IDを検索用集合にし、不正なalignmentを持つsegmentを探す]
    //   引数: [segments: 補完・補正済みsegment, segmentIds: 学習対象のsegment ID]
    //   エラー: [不正なalignmentがある場合にInvalidOperationExceptionを送出する]
    // }
    private static void EnsureSingingAlignment(IEnumerable<UniversalVoiceSegment> segments,
        IReadOnlyCollection<string> segmentIds)
    {
        // 対象IDの集合を一度だけ作り、全segmentとの照合を線形時間で行う。
        var selectedIds = segmentIds.ToHashSet(StringComparer.Ordinal);
        var unaligned = segments.FirstOrDefault(segment => selectedIds.Contains(segment.SegmentId) &&
            !segment.HasValidSingingTrainingAlignment());
        if (unaligned is not null)
            throw new InvalidOperationException(
                $"セグメント {unaligned.SegmentId} に検証済みの音素単位forced alignmentがありません。" +
                "推定時刻を含む素材は歌唱学習ジョブへ登録できません。");
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
