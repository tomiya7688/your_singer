using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [TrainingJobRunner: 学習1件の実行を挟み、Running・Succeeded・Failed・Canceledを保存済みjobへ記録する]
//   フィールド: [planning: 学習状態を保存するjob管理サービス]
//   処理: [1: Runningを記録する 2: 渡された学習処理を実行する 3: 成功・取消・失敗を区別して終了状態を記録する]
// }
public sealed class TrainingJobRunner(TrainingJobPlanningService planning)
{
    // {
    //   責務: [RunAsync: 学習処理を実行し、結果に応じた終了状態を必ず保存する]
    //   引数: [workspace: 作業領域, batchId/jobId: 対象job, train: 学習処理(ログのパスを返す。取消時はOperationCanceledExceptionで終わる)]
    //   戻り値: [state: 記録した終了状態(Succeeded / Failed / Canceled)]
    //   副作用: [jobの状態・エラーメッセージ・開始/完了時刻を保存する]
    //   エラー: [Running記録に失敗したとき(許可されない遷移など)は学習を起動せずそのまま送出する]
    //   補足: [終了状態の保存は取消済みトークンでも実行するため、取消後も Canceled が残る。失敗の理由は ErrorMessage に残し、成果物は公開しない]
    // }
    public async Task<TrainingJobState> RunAsync(ProjectWorkspace workspace, string batchId, string jobId,
        Func<CancellationToken, Task<string>> train, CancellationToken cancellationToken = default)
    {
        await planning.UpdateStateAsync(workspace, batchId, jobId, TrainingJobState.Running,
            cancellationToken: cancellationToken);
        TrainingJobState final;
        string? message = null;
        try
        {
            await train(cancellationToken);
            final = TrainingJobState.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            final = TrainingJobState.Canceled;
            message = "学習が取り消されました。子プロセスは回収済みです。部分ログは診断用に残ります。";
        }
        catch (Exception error)
        {
            final = TrainingJobState.Failed;
            message = error.Message;
        }
        // 取消済みトークンでは保存できないため、終了状態の保存には別のトークンを使う。
        await planning.UpdateStateAsync(workspace, batchId, jobId, final, errorMessage: message,
            cancellationToken: CancellationToken.None);
        return final;
    }
}
