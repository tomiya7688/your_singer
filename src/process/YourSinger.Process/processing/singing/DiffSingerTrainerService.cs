using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Singing;

public sealed class DiffSingerTrainerService
{
    private readonly MlWorkerClient _workerClient;

    public DiffSingerTrainerService(MlWorkerClient workerClient)
    {
        _workerClient = workerClient;
    }

    public async Task<string> TrainAsync(
        ProjectWorkspace workspace,
        TrainingJobRecord job,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱学習ジョブのみDiffSinger trainerを実行できます。");

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
                output_dir = outputDirectory
            },
            cancellationToken);

        if (!result.TryGetProperty("log_path", out JsonElement logPath))
            throw new InvalidOperationException("DiffSinger trainer結果にlog_pathがありません。");

        return logPath.GetString()
            ?? throw new InvalidOperationException("DiffSinger trainer log pathが空です。");
    }
}
