using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Talk;

public sealed class StyleBertVits2TrainerService
{
    private readonly MlWorkerClient _workerClient;

    public StyleBertVits2TrainerService(MlWorkerClient workerClient)
    {
        _workerClient = workerClient;
    }

    public async Task<string> TrainAsync(
        ProjectWorkspace workspace,
        TrainingJobRecord job,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Talk)
            throw new InvalidOperationException(
                "トーク学習ジョブのみStyle-Bert-VITS2 trainerを実行できます。");

        var trainerRoot = Path.Combine(
            AppContext.BaseDirectory,
            "workers",
            "style-bert-vits2");

        var outputDirectory = Path.Combine(
            workspace.RootPath,
            "training",
            "style-bert-vits2",
            job.JobId,
            "trainer");

        var result = await _workerClient.SendAsync(
            "train_style_bert_vits2",
            new
            {
                trainer_root = trainerRoot,
                model_name = modelName,
                output_dir = outputDirectory
            },
            cancellationToken);

        if (!result.TryGetProperty("log_path", out JsonElement logPath))
            throw new InvalidOperationException(
                "Style-Bert-VITS2 trainer結果にlog_pathがありません。");

        return logPath.GetString()
            ?? throw new InvalidOperationException(
                "Style-Bert-VITS2 trainer log pathが空です。");
    }
}
