using Avalonia.Controls;

namespace YourSinger.App;

public sealed partial class MainWindow
{
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (TrainingList.Parent is not StackPanel panel) return;
        // 既存の学習画面に実験的な候補生成の入口を追加する。
        if (panel.Children.Count > 2 && panel.Children[2] is TextBlock description)
            description.Text = "有効にすると短い音高欠損の補完と低信頼区間の除外を反映します。音素補完候補は、対象話者の学習済みモデルから別途生成・検証・採用する実験機能です。元録音と手動編集は保持します。";
        var button = new Button { Content = "音素補完候補（実験）" };
        panel.Children.Insert(3, button);
        button.Click += async (_, _) =>
        {
            if (_workspace is null)
            {
                StatusText.Text = "先にプロジェクトの素材を読み込み、解析してください。";
                return;
            }
            button.IsEnabled = false;
            try { await new PhonemeSupplementWindow(_workspace).ShowDialog(this); }
            catch (Exception) { StatusText.Text = "音素補完の画面を開けませんでした。保存データを確認してください。"; }
            finally { button.IsEnabled = true; }
        };
    }
}
