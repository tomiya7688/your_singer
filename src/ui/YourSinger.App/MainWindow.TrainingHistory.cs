using Avalonia.Interactivity;

namespace YourSinger.App;

public sealed partial class MainWindow
{
    private bool _trainingHistoryOpen;

    private async void OnOpenTrainingHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (_trainingHistoryOpen) return;
        _trainingHistoryOpen = true;
        try
        {
            var history = new TrainingHistoryWindow(_workspace);
            await history.ShowDialog(this);
            if (history.LastRecreatedBatchId is { } batchId)
                StatusText.Text = $"新しい学習ジョブを作成しました。学習は開始していません。{batchId}";
        }
        catch (Exception)
        {
            StatusText.Text = "学習履歴を開けませんでした。プロジェクトの保存先を確認してください。";
        }
        finally { _trainingHistoryOpen = false; }
    }
}
