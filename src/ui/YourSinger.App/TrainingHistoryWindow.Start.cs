using Avalonia.Interactivity;
using YourSinger.Data.Models;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Talk;
using YourSinger.Process.Processing.Training;

namespace YourSinger.App;

// { 責務: [TrainingHistoryWindow(学習開始): 選択したバッチの待機中トークジョブを学習し、取消と終了状態を扱う] }
public sealed partial class TrainingHistoryWindow
{
    private CancellationTokenSource? _training;

    // { 責務: [PendingTalkJob: バッチ内の最初の待機中トークジョブを返す(無ければnull)] }
    private static TrainingJobRecord? PendingTalkJob(TrainingJobBatch batch) =>
        batch.Jobs.FirstOrDefault(x => x.Target == TrainingTarget.Talk && x.State == TrainingJobState.Pending);

    // {
    //   責務: [OnStartTalkClick: 待機中のトークジョブをパイプラインで学習する]
    //   処理: [1: 同梱Trainerを確認 2: パイプラインを組む 3: 実行し、結果(成功・失敗・取消・実行不可)を表示する]
    //   副作用: [jobの状態を保存する。実行不可の場合は学習入力もTrainerも作らずPendingのまま理由を表示する]
    // }
    private async void OnStartTalkClick(object? sender, RoutedEventArgs e)
    {
        if (_workspace is not { } workspace || BatchList.SelectedItem is not BatchRow row ||
            PendingTalkJob(row.Batch) is not { } job) return;
        var trainerRoot = Path.Combine(AppContext.BaseDirectory, "workers", "style-bert-vits2");
        var lockPath = Path.Combine(trainerRoot, "resource-lock.json");
        if (!File.Exists(lockPath))
        {
            HistoryStatusText.Text = "トーク学習用の同梱Trainerが見つかりません。配布物を確認してください。";
            return;
        }
        await RunAsync(async token =>
        {
            using var training = CancellationTokenSource.CreateLinkedTokenSource(token);
            _training = training;
            CancelTrainingButton.IsEnabled = true;
            try
            {
                HistoryStatusText.Text = "学習の条件を確認しています…";
                var outcome = await CreatePipeline().RunAsync(workspace, row.Batch.BatchId, job.JobId,
                    trainerRoot, lockPath, training.Token);
                HistoryStatusText.Text = outcome.State switch
                {
                    TrainingJobState.Succeeded => "学習が完了しました。モデルの品質検証は未実施です。",
                    TrainingJobState.Failed => "学習に失敗しました。ログを確認してください。",
                    TrainingJobState.Canceled => "学習を取り消しました。",
                    _ => "学習を始められません: " + string.Join(" / ", outcome.StopReasons)
                };
            }
            finally
            {
                _training = null;
                CancelTrainingButton.IsEnabled = false;
            }
            await RefreshHistoryAsync(row.Batch.BatchId, CancellationToken.None);
        });
    }

    // { 責務: [OnCancelTrainingClick: 実行中の学習を取り消す(Trainerの子プロセスごと停止される)] }
    private void OnCancelTrainingClick(object? sender, RoutedEventArgs e)
    {
        _training?.Cancel();
        HistoryStatusText.Text = "学習を取り消しています…";
    }

    // { 責務: [CreatePipeline: 現在のプロジェクトでトーク学習パイプラインを組み立てる] }
    private TalkTrainingPipeline CreatePipeline()
    {
        var dataset = new UniversalVoiceDatasetRepository();
        var correction = new AutoCorrectionService(dataset, new AutoCorrectionRepository());
        var worker = new MlWorkerClient();
        var preflight = new TrainingPreflightService(
            new TalkTrainingStrategyPlanner(BuiltInTalkStrategies.CreateRegistry(), new TexturePresetRegistry()),
            new SingingTrainingStrategyPlanner(BuiltInSingingStrategies.CreateRegistry(), new TexturePresetRegistry()), _jobs);
        var planning = new TrainingJobPlanningService(dataset, correction, _jobs);
        return new TalkTrainingPipeline(
            new SpeakerInventoryProvider(correction, new ContentClassificationRepository()),
            new TrainingExecutionService(preflight, new TrainingJobRunner(planning), _jobs),
            new StyleBertVits2DatasetBuilder(dataset), new StyleBertVits2TrainerService(worker), _jobs);
    }
}
