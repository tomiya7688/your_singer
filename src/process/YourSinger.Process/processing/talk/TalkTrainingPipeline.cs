using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Talk;

// {
//   責務: [TalkTrainingPipeline: トーク学習1件を、棚卸し・GPU確認・初期重みの確認から学習入力の作成・学習・終了状態の記録まで進める]
//   フィールド: [inventory: 話者の棚卸し, execution: 計画解決から終了状態の記録, datasetBuilder: 学習入力の作成, trainer: Trainerの実行,
//     detectHardware: GPU情報の取得(テストで置き換える), scanCheckpoints: 初期重みの棚卸し(テストで置き換える)]
//   処理: [1: 棚卸しとGPU・初期重みを集める 2: 計画を解決し実行不可なら止める 3: 実行中に学習入力を作りTrainerを実行する]
// }
public sealed class TalkTrainingPipeline(
    SpeakerInventoryProvider inventory, TrainingExecutionService execution, StyleBertVits2DatasetBuilder datasetBuilder,
    StyleBertVits2TrainerService trainer, TrainingJobRepository jobs,
    Func<CancellationToken, Task<HardwareInfo>>? detectHardware = null,
    Func<CancellationToken, Task<IReadOnlyList<ResolverCheckpoint>>>? scanCheckpoints = null)
{
    // {
    //   責務: [RunAsync: Pendingのトークjobを学習し、結果を保存する]
    //   引数: [workspace: 作業領域, batchId/jobId: 対象job, trainerRoot: 同梱Trainerのフォルダ, lockPath: 固定記録のパス]
    //   戻り値: [outcome: 終了状態と計画。実行不可なら State=null で学習入力もTrainerも作らない]
    //   エラー: [jobが無い・トークでない場合 InvalidOperationException。学習入力の作成やTrainerの失敗はFailedとして記録される]
    //   補足: [モデル名は TrainingModelNaming に従う。学習入力は計画が実行可能と確定した後にだけ作る]
    // }
    public async Task<TrainingExecutionOutcome> RunAsync(ProjectWorkspace workspace, string batchId, string jobId,
        string trainerRoot, string lockPath, CancellationToken cancellationToken = default)
    {
        var batch = await jobs.LoadBatchAsync(workspace, batchId, cancellationToken)
            ?? throw new InvalidOperationException("学習ジョブのバッチがありません。");
        var job = batch.Jobs.FirstOrDefault(x => x.JobId == jobId)
            ?? throw new InvalidOperationException("学習ジョブがありません。");
        if (job.Target != TrainingTarget.Talk)
            throw new InvalidOperationException("トーク学習ジョブではありません。");
        var speakerInventory = await inventory.BuildAsync(workspace, job, cancellationToken);
        var hardware = await (detectHardware?.Invoke(cancellationToken) ?? HardwareProbe.DetectAsync(cancellationToken));
        var checkpoints = await (scanCheckpoints?.Invoke(cancellationToken)
            ?? TalkCheckpointInventory.ScanAsync(trainerRoot, lockPath, cancellationToken));
        var modelName = TrainingModelNaming.ModelName(job.SpeakerId, job.JobId);
        var run = trainer.AsExecution(workspace, modelName);
        return await execution.ExecuteAsync(workspace, batchId, jobId, speakerInventory,
            new PreflightEnvironment(hardware, checkpoints), async (fixedJob, token) =>
            {
                // 実行可能と確定した後にだけ、学習入力を作る。失敗はFailedとして記録される。
                await datasetBuilder.BuildAsync(workspace, fixedJob, modelName, token);
                return await run(fixedJob, token);
            }, cancellationToken);
    }
}
