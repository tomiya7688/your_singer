using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.App;

/// <summary>生成した候補を観測データ・学習データとは分離して確認する画面。</summary>
public sealed class PhonemeCandidateWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Text = "参照候補を確認しています。", TextWrapping = TextWrapping.Wrap };
    private readonly Button _generate = new() { Content = "補完音声の候補を生成", IsEnabled = false };
    private readonly Button _open = new() { Content = "候補の保存先を開く", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "閉じる" };
    private readonly ProgressBar _progress = new() { IsIndeterminate = false };
    private readonly PhonemeCandidateService _service = new(new UniversalVoiceDatasetRepository(), new CompletionWorkerProcess());
    private PhonemeCandidatePlan? _plan;
    private string? _output;
    private bool _closed;
    private bool _busy;

    public PhonemeCandidateWindow(ProjectWorkspace workspace, string speakerId)
    {
        Title = "不足音素の補完候補";
        Width = 700; Height = 590; MinWidth = 540; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "不足音素を含む発話候補", FontSize = 24, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "選択した話者の会話を参照し、不足音素を含む文章の音声候補をローカルで生成します。歌唱音声の生成ではありません。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    _summary,
                    new TextBlock
                    {
                        Text = "候補は生成音声として別保存します。発音と声の一致はまだ自動検証しません。生成しただけでは学習へ追加せず、録音済みのカバレッジにも数えません。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "補完専用ワーカーと対応モデルの同梱が必要です。この操作で外部への音声送信、モデルの自動取得、学習の開始は行いません。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    _generate, _progress, _status,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _open, _cancel } }
                }
            }
        };
        Opened += async (_, _) =>
        {
            try
            {
                var token = _lifetime.Token;
                var plan = await Task.Run(() => _service.PlanAsync(workspace, speakerId, token), token);
                if (_closed) return;
                _plan = plan;
                _summary.Text = $"話者: {plan.SpeakerId}\n参照区間: {plan.ReferenceSegmentId}\n参照文: {plan.ReferenceText}\n不足音素: {string.Join("、", plan.MissingPhonemes)}";
                _status.Text = "内容を確認してから生成してください。1回につき最大4文です。";
                _generate.IsEnabled = true;
            }
            catch (Exception error) { if (!_closed) ShowError(error); }
        };
        _generate.Click += async (_, _) =>
        {
            if (_busy || _plan is not { } plan) return;
            _busy = true; _generate.IsEnabled = false; _open.IsEnabled = false;
            _cancel.Content = "中止して閉じる"; _progress.IsIndeterminate = true;
            _status.Text = "不足音素を含む音声候補を生成しています。";
            try
            {
                var token = _lifetime.Token;
                var result = await Task.Run(() => _service.GenerateAsync(workspace, plan, token), token);
                if (_closed) return;
                _output = Path.GetDirectoryName(result.ManifestPath);
                _open.IsEnabled = true;
                _status.Text = $"{result.CandidateCount}件の未採用候補を保存しました。発音・声質の検証と学習への採用はまだ行っていません。\n{result.ManifestPath}";
            }
            catch (Exception error) { if (!_closed) ShowError(error); }
            finally
            {
                _busy = false;
                if (!_closed)
                {
                    _progress.IsIndeterminate = false; _cancel.Content = "閉じる";
                    // 失敗や表示後の二重生成は、画面を開き直して参照を再確認する。
                    _generate.IsEnabled = false;
                }
            }
        };
        _open.Click += (_, _) =>
        {
            if (_output is null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_output) { UseShellExecute = true }); }
            catch (Exception) { _status.Text = "保存先を開けませんでした。表示されたパスを確認してください。"; }
        };
        _cancel.Click += (_, _) => Close();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private void ShowError(Exception error) => _status.Text = error switch
    {
        OperationCanceledException => "生成を中止しました。候補は学習に使われません。",
        FileNotFoundException => "参照音声・補完ワーカー・対応モデルのいずれかがありません。補完対応の配布構成を確認してください。",
        UnauthorizedAccessException => "プロジェクトとモデルの読み取り・書き込み権限を確認してください。",
        _ => $"候補を生成できませんでした。{error.Message}"
    };
}
