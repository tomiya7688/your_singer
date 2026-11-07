using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

// {
//   責務: [SingingTrainingPipeline: 歌唱学習1件を、棚卸し・GPU・初期重みの確認から、学習入力・binarization・学習・終了状態の記録まで進める]
//   フィールド: [inventory/execution/jobs: トーク側と同じ共通部品, datasetBuilder/acousticInput/binarize/trainer: 歌唱の各段階,
//     detectHardware/scanCheckpoints: テストで置き換える取得処理, durationInput/durationBinarize: 指定時だけ音響学習の後にDurationを学習する]
//   処理: [1: 棚卸しとGPU・初期重みを集める 2: 計画を解決し実行不可なら止める 3: 実行中に学習入力→音響入力→binarization→学習を順に行う 4: Duration段階が指定されていれば、音響学習の後にDuration入力→binarization→学習を行う]
// }
public sealed class SingingTrainingPipeline(
    SpeakerInventoryProvider inventory, TrainingExecutionService execution, DiffSingerDatasetBuilder datasetBuilder,
    DiffSingerAcousticInputService acousticInput, DiffSingerBinarizeService binarize, DiffSingerTrainerService trainer,
    TrainingJobRepository jobs,
    Func<CancellationToken, Task<HardwareInfo>>? detectHardware = null,
    Func<CancellationToken, Task<IReadOnlyList<ResolverCheckpoint>>>? scanCheckpoints = null,
    DiffSingerDurationInputService? durationInput = null,
    DiffSingerDurationBinarizeService? durationBinarize = null)
{
    public static readonly string VocoderRelativePath = Path.Combine("models", "vocoder", "nsf_hifigan", "model.ckpt");

    // {
    //   責務: [RequireVocoder: 音響モデルの学習に必須のvocoder学習済みモデルがbundleにあることを確認する]
    //   エラー: [無いとき InvalidOperationException(置き場所と、本アプリはまだ取得・同梱していない旨を示す)]
    // }
    public static void RequireVocoder(string trainerRoot)
    {
        if (!File.Exists(Path.Combine(trainerRoot, VocoderRelativePath)))
            throw new InvalidOperationException(
                $"音響モデルの学習にはvocoderの学習済みモデルが必要です。{VocoderRelativePath} に置いてください(CC BY-NC-SA 4.0・非商用のため本アプリは同梱しません。OpenVPI vocoders の pc-nsf-hifigan-44.1k-hop512-128bin-2025.02 リリースから取得してください)。");
    }

    // {
    //   責務: [RunAsync: Pendingの歌唱jobを学習し、結果を保存する]
    //   引数: [workspace: 作業領域, batchId/jobId: 対象job, trainerRoot: 同梱DiffSingerのフォルダ, checkpointDirectory: 歌唱の初期重みの置き場]
    //   戻り値: [outcome: 終了状態と計画。実行不可なら State=null で学習入力もTrainerも作らない]
    //   エラー: [jobが無い・歌唱でない場合 InvalidOperationException。各段階の失敗はFailedとして記録される]
    // }
    public async Task<TrainingExecutionOutcome> RunAsync(ProjectWorkspace workspace, string batchId, string jobId,
        string trainerRoot, string checkpointDirectory, CancellationToken cancellationToken = default)
    {
        var batch = await jobs.LoadBatchAsync(workspace, batchId, cancellationToken)
            ?? throw new InvalidOperationException("学習ジョブのバッチがありません。");
        var job = batch.Jobs.FirstOrDefault(x => x.JobId == jobId)
            ?? throw new InvalidOperationException("学習ジョブがありません。");
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱学習ジョブではありません。");
        var speakerInventory = await inventory.BuildAsync(workspace, job, cancellationToken);
        var hardware = await (detectHardware?.Invoke(cancellationToken) ?? HardwareProbe.DetectAsync(cancellationToken));
        var checkpoints = await (scanCheckpoints?.Invoke(cancellationToken)
            ?? SingingCheckpointInventory.ScanAsync(checkpointDirectory, cancellationToken));
        return await execution.ExecuteAsync(workspace, batchId, jobId, speakerInventory,
            new PreflightEnvironment(hardware, checkpoints), async (fixedJob, token) =>
            {
                // 実行可能と確定した後にだけ入力を作る。どの段階の失敗もFailedとして記録される。
                // 音響モデルの学習はvocoderの学習済みモデルを必須とする。入力作成の前に確認し、無ければ理由付きで失敗にする。
                RequireVocoder(trainerRoot);
                await datasetBuilder.BuildAsync(workspace, fixedJob, token);
                await acousticInput.PrepareAsync(workspace, fixedJob, token);
                var configPath = await binarize.BinarizeAsync(workspace, fixedJob, trainerRoot, token);
                var acousticLog = await trainer.TrainAsync(workspace, fixedJob, configPath, token);
                if (durationInput is null || durationBinarize is null)
                    return acousticLog;
                // Durationは音響モデルと別の出力先で学習する。失敗は例外としてjobをFailedにする(音響の成果は出力先に残る)。
                await durationInput.PrepareAsync(workspace, fixedJob, token);
                var durationConfig = await durationBinarize.BinarizeAsync(workspace, fixedJob, trainerRoot, token);
                await trainer.TrainDurationAsync(workspace, fixedJob, durationConfig, token);
                return acousticLog;
            }, cancellationToken);
    }
}
