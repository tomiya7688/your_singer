using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Talk;

// {
//   責務: [StyleBertVits2TrainerService: トーク学習ジョブの入力を専用Trainerへ渡す]
//   フィールド: [_workerClient: 学習要求を送るMLワーカー]
// }
public sealed class StyleBertVits2TrainerService
{
    private readonly MlWorkerClient _workerClient;

    // { 責務: [StyleBertVits2TrainerService: 学習要求の送信先を保持する] }
    public StyleBertVits2TrainerService(MlWorkerClient workerClient)
    {
        _workerClient = workerClient;
    }

    // {
    //   責務: [TrainAsync: 作成済みのトーク学習入力でTrainerを実行する]
    //   処理: [1: ジョブと入力を確認する 2: 専用環境へ入力を渡す 3: ログの参照を返す]
    //   エラー: [入力不足・Trainer失敗・成果物不足で失敗する]
    // }
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

        var datasetDirectory = Path.Combine(
            workspace.RootPath,
            "training",
            "style-bert-vits2",
            TrainingInputDirectory.RequireComponent(job.JobId));
        TrainingInputDirectory.RequireComponent(modelName);
        if (!File.Exists(Path.Combine(datasetDirectory, "dataset.json")) ||
            !File.Exists(Path.Combine(datasetDirectory, "esd.list")))
            throw new InvalidOperationException("トーク学習入力を作成してから学習を実行してください。");
        var outputDirectory = Path.Combine(datasetDirectory, "trainer");

        var result = await _workerClient.SendAsync(
            "train_style_bert_vits2",
            new
            {
                trainer_root = trainerRoot,
                model_name = modelName,
                dataset_dir = datasetDirectory,
                job_id = job.JobId,
                dataset_fingerprint = job.DatasetFingerprint,
                auto_correction_enabled = job.AutoCorrectionEnabled,
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
