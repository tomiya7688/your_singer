using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

public sealed class DiffSingerTrainerService
{
    private readonly MlWorkerClient _workerClient;

    public DiffSingerTrainerService(MlWorkerClient workerClient)
    {
        _workerClient = workerClient;
    }

    // {
    //   責務: [AsExecution: TrainingExecutionService に渡す学習処理を作る]
    //   引数: [workspace: 作業領域, configPath: 学習設定]
    //   戻り値: [train: 固定済みjobを受けてTrainAsyncを実行し、ログのパスを返す処理]
    // }
    public Func<TrainingJobRecord, CancellationToken, Task<string>> AsExecution(ProjectWorkspace workspace, string configPath) =>
        (job, token) => TrainAsync(workspace, job, configPath, token);

    public async Task<string> TrainAsync(
        ProjectWorkspace workspace,
        TrainingJobRecord job,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱学習ジョブのみDiffSinger trainerを実行できます。");
        // 学習前に、解決済みで実行可能な計画を要求する(未解決・実行不可は起動しない)。
        YourSinger.Process.Processing.Training.TrainingPreflightService.RequireResolvedPlan(job);

        var trainerRoot = Path.Combine(AppContext.BaseDirectory, "workers", "diffsinger");
        var outputDirectory = Path.Combine(
            workspace.RootPath,
            "training",
            "diffsinger",
            job.JobId,
            "trainer");

        var result = await _workerClient.SendAsync(
            "train_diffsinger",
            new
            {
                trainer_root = trainerRoot,
                config_path = configPath,
                exp_name = job.JobId,
                output_dir = outputDirectory,
                job_id = job.JobId,
                seed = job.Seed,
                resolved_training_mode = job.ResolvedPlan?.ResolvedTrainingMode,
                initialization = InitializationPayload(workspace, job)
            },
            cancellationToken);

        if (!result.TryGetProperty("log_path", out JsonElement logPath))
            throw new InvalidOperationException("DiffSinger trainer結果にlog_pathがありません。");

        // worker の実行記録を、計画と実ファイルに照合する。矛盾・欠損は失敗にし、成功として残さない。
        TrainingRunReportVerifier.VerifyAndRecord(job, result, outputDirectory);

        return logPath.GetString()
            ?? throw new InvalidOperationException("DiffSinger trainer log pathが空です。");
    }

    // {
    //   責務: [TrainDurationAsync: Duration(variance)モデルを、音響モデルとは別の出力先と実験名で学習する]
    //   引数: [workspace: 作業領域, job: 解決済み計画を持つ歌唱job, configPath: Duration binarization で作った学習設定]
    //   戻り値: [logPath: 学習ログのパス]
    //   エラー: [歌唱job以外・計画が未解決または実行不可・worker異常・ログ欠落で失敗する]
    //   補足: [
    //     解決済み計画は音響モデル(builtin.diffsinger.acoustic)のものを実行可否の関門としてだけ使う。
    //     Durationの初期化方式は計画に含まれないため initialization は渡さず、固定版の設定どおり新規学習になる(記録は run report に出ない)。
    //     重みmanifest(区間weight)は音響学習と同じものを、同名のクリップに対して使う。
    //   ]
    // }
    public async Task<string> TrainDurationAsync(ProjectWorkspace workspace, TrainingJobRecord job, string configPath,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱学習ジョブのみDuration trainerを実行できます。");
        TrainingPreflightService.RequireResolvedPlan(job);
        var jobId = TrainingInputDirectory.RequireComponent(job.JobId);
        var outputDirectory = Path.Combine(workspace.RootPath, "training", "diffsinger", jobId, "trainer-duration");
        var result = await _workerClient.SendAsync("train_diffsinger", new
        {
            trainer_root = Path.Combine(AppContext.BaseDirectory, "workers", "diffsinger"),
            config_path = configPath,
            exp_name = jobId + "-duration",
            output_dir = outputDirectory,
            job_id = jobId,
            seed = job.Seed
        }, cancellationToken);
        return result.TryGetProperty("log_path", out var logPath) && logPath.GetString() is { Length: > 0 } path
            ? path
            : throw new InvalidOperationException("Duration trainer結果にlog_pathがありません。");
    }

    // {
    //   責務: [InitializationPayload: 解決済み計画の初期化方式を、workerへ渡す形にする(重みのパスはワークスペース内の絶対パス)]
    //   戻り値: [payload: type と components(name・sha256・絶対path)。計画が無ければ null(旧形式の要求)]
    //   エラー: [初期重みのパスがワークスペース外を指すとき InvalidOperationException]
    // }
    public static object? InitializationPayload(ProjectWorkspace workspace, TrainingJobRecord job)
    {
        var initialization = job.ResolvedPlan?.Stages.FirstOrDefault(x => x.Initialization is not null)?.Initialization;
        if (initialization is null) return null;
        var root = Path.GetFullPath(workspace.RootPath);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return new
        {
            type = initialization.Type,
            components = initialization.Components.Select(x =>
            {
                var full = Path.GetFullPath(x.Path, root);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"初期重みがワークスペース外を指しています: {x.Name}");
                return new { name = x.Name, sha256 = x.Sha256, path = full };
            }).ToArray()
        };
    }
}
