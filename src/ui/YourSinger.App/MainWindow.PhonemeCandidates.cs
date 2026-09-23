using Avalonia.Interactivity;

namespace YourSinger.App;

public sealed partial class MainWindow
{
    private bool _phonemeCandidateWindowOpen;

    private async void OnOpenPhonemeCandidatesClick(object? sender, RoutedEventArgs e)
    {
        if (_phonemeCandidateWindowOpen) return;
        if (_workspace is not { } workspace)
        {
            StatusText.Text = "先にプロジェクトを作成して解析してください。";
            return;
        }
        var speakers = _speakerChecks.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToArray();
        if (speakers.Length != 1)
        {
            StatusText.Text = "補完候補を生成する話者を1人選択してください。";
            return;
        }
        _phonemeCandidateWindowOpen = true;
        try { await new PhonemeCandidateWindow(workspace, speakers[0]).ShowDialog(this); }
        catch (Exception) { StatusText.Text = "補完候補の画面を開けませんでした。保存先を確認してください。"; }
        finally { _phonemeCandidateWindowOpen = false; }
    }
}
