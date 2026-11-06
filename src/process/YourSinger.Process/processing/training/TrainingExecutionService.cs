using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Training;

// { 責務: [TrainingExecutionOutcome: 学習実行の結果。計画が実行不可で始めなかった場合は State が null で、理由を持つ] }
public sealed record TrainingExecutionOutcome(
    TrainingJobState? State, TrainingResolvedPlanRecord Plan, IReadOnlyList<string> StopReasons);

// {
//   責務: [TrainingExecutionService: Preflight → 棚卸し照合 → Trainer実行 → 終了状態の記録を順に行う]
//   フィールド: [preflight: 学習計画の解決, runner: 実行と終了状態の記録]
//   処理: [1: 計画を解決しjobに固定する 2: 実行不可なら学習を始めず理由を返す 3: 棚卸しの版を照合する 4: 保存済みjobでTrainerを実行する]
// }
public sealed class TrainingExecutionService(TrainingPreflightService preflight, TrainingJobRunner runner,
    TrainingJobRepository jobRepository)
{
    // {
    //   責務: [ExecuteAsync: 1件の学習を計画解決から終了状態の記録まで進める]
    //   引数: [workspace: 作業領域, batchId/jobId: 対象job, inventory: 最新の棚卸し, environment: GPUと初期重み,
    //          train: 解決済みjobでTrainerを実行する処理(ログのパスを返す。取消時はOperationCanceledException)]
    //   戻り値: [outcome: 終了状態と解決済み計画。実行不可なら State=null でjobはPendingのまま]
    //   エラー: [旧job・Pending以外・話者不一致は Preflight が拒否する。Running記録に失敗した場合はそのまま送出する]
    //   補足: [実行不可はTrainerの失敗ではないため Failed にしない。理由は計画(StopReasons)に残り、条件を直して新しいPreflightで再開できる]
    // }
    public async Task<TrainingExecutionOutcome> ExecuteAsync(ProjectWorkspace workspace, string batchId, string jobId,
        SpeakerDataInventory inventory, PreflightEnvironment environment,
        Func<TrainingJobRecord, CancellationToken, Task<string>> train, CancellationToken cancellationToken = default)
    {
        var plan = await preflight.PreflightAsync(workspace, batchId, jobId, inventory, environment, cancellationToken);
        if (!plan.Executable) return new TrainingExecutionOutcome(null, plan, plan.StopReasons);

        // 保存済みのjob(計画が固定されたもの)を読み直し、棚卸しが変わっていないことを確認してから実行する。
        var job = await LoadJobAsync(workspace, batchId, jobId, cancellationToken);
        TrainingPreflightService.RequireExecutable(job, inventory.InventoryRevision);
        var state = await runner.RunAsync(workspace, batchId, jobId, token => train(job, token), cancellationToken);
        return new TrainingExecutionOutcome(state, plan, []);
    }

    private async Task<TrainingJobRecord> LoadJobAsync(ProjectWorkspace workspace, string batchId, string jobId,
        CancellationToken cancellationToken)
    {
        var batch = await jobRepository.LoadBatchAsync(workspace, batchId, cancellationToken)
            ?? throw new InvalidOperationException("学習ジョブのバッチがありません。");
        return batch.Jobs.FirstOrDefault(x => x.JobId == jobId)
            ?? throw new InvalidOperationException("学習ジョブがありません。");
    }
}
