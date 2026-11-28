using Avalonia.Interactivity;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.App;

public sealed partial class MainWindow
{
    private async void OnSetEmotionClick(object? sender, RoutedEventArgs e)
    {
        var name = EmotionNameBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            StatusText.Text = "付ける感情の名前を入力してください。";
            return;
        }
        await ApplyEmotionAsync(name);
    }

    private async void OnClearEmotionClick(object? sender, RoutedEventArgs e) =>
        await ApplyEmotionAsync(null);

    // {
    //   責務: [ApplyEmotionAsync: 分類一覧で選んだ区間の感情ラベルを付ける(名前あり)か外す(null)]
    //   処理: [1: 区間が選ばれているか確認 2: 利用者の指定として保存(再解析後も再適用される) 3: 一覧を更新して結果を表示]
    //   引数: [emotionName: 付ける感情名。null なら外す]
    //   戻り値: []
    //   副作用: [データセットと上書き履歴を保存する]
    //   エラー: [名前が不正・区間が無い場合は理由を状態欄に出し、何も保存しない]
    // }
    private async Task ApplyEmotionAsync(string? emotionName)
    {
        if (_workspace is null)
            return;
        if (ClassificationList.SelectedItem is not ClassificationUiItem item)
        {
            StatusText.Text = "感情を付ける区間を一覧から選んでください。";
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await new UniversalFeatureOverrideService(_datasetRepository)
                .SetEmotionAsync(_workspace, item.SegmentId, emotionName);
            await RefreshClassificationsAsync();
            StatusText.Text = emotionName is null
                ? "感情ラベルを外しました(指定なしに戻りました)。"
                : $"感情「{emotionName}」を付けました。";
        }, "感情ラベルの保存に失敗しました。");
    }
}
