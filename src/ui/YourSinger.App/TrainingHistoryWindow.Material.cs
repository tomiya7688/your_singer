using Avalonia.Interactivity;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.App;

public sealed partial class TrainingHistoryWindow
{
    // {
    //   責務: [OnShowMaterialClick: 選択中のジョブ一式の話者ごとの素材量(発話教師・歌唱教師・声質参照)を詳細欄に追記する]
    //   処理: [各ジョブと同じ補正設定で棚卸しを作り、MaterialAmountPresenter で日本語にする。内容分類が未保存の話者は理由を示す]
    //   副作用: [BatchDetailText と HistoryStatusText を更新する。保存データは変更しない]
    // }
    private async void OnShowMaterialClick(object? sender, RoutedEventArgs e)
    {
        if (_workspace is not { } workspace || BatchList.SelectedItem is not BatchRow row) return;
        await RunAsync(async token =>
        {
            var provider = new SpeakerInventoryProvider(
                new AutoCorrectionService(new UniversalVoiceDatasetRepository(), new AutoCorrectionRepository()),
                new ContentClassificationRepository());
            var lines = new List<string> { "【素材量】" };
            // 同じ話者・補正設定は1回だけ集計する。
            foreach (var job in row.Batch.Jobs.DistinctBy(x => (x.SpeakerId, x.AutoCorrectionEnabled)))
            {
                try
                {
                    var inventory = await provider.BuildAsync(workspace, job, token);
                    lines.AddRange(MaterialAmountPresenter.Describe(inventory));
                }
                catch (InvalidOperationException exception)
                {
                    lines.Add($"話者 {job.SpeakerId}: 素材量を数えられません。{exception.Message}");
                }
            }
            token.ThrowIfCancellationRequested();
            BatchDetailText.Text += "\n\n" + string.Join("\n", lines);
            HistoryStatusText.Text = "素材量を表示しました。足りるかどうかは、学習開始時の確認で判断します。";
        });
    }
}
