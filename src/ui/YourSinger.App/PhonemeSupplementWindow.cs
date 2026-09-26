using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Dataset.Completion;

namespace YourSinger.App;

public sealed class PhonemeSupplementWindow : Window
{
    private readonly ProjectWorkspace _workspace;
    private readonly PhonemeSupplementService _service = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _panel = new() { Margin = new Thickness(24), Spacing = 10 };
    private readonly ComboBox _reference = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _modelSpeaker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _modelPath = new() { IsReadOnly = true };
    private readonly TextBox _text = new() { MaxLength = 120, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _candidates = new() { Height = 170 };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _runtimeStatus = new() { TextWrapping = TextWrapping.Wrap, Text = "補完環境を確認していません。" };
    private readonly TextBlock _needs = new() { TextWrapping = TextWrapping.Wrap, Text = "参照区間を選ぶと補助対象音素を表示します。" };
    private UniversalVoiceDatasetRecord? _snapshot;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Text = "準備完了" };
    private readonly CheckBox _reviewed = new() { Content = "生成音声を試聴し、発音と話者を確認しました" };
    private readonly ProgressBar _progress = new() { IsVisible = false, IsIndeterminate = true };
    private bool _busy;
    private bool _closed;

    public PhonemeSupplementWindow(ProjectWorkspace workspace)
    {
        _workspace = workspace;
        Title = "音素補完候補（実験）"; Width = 800; Height = 790; MinWidth = 600; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        AddText("対象話者の学習済みStyle-Bert-VITS2モデルから会話の補完候補を生成します。初回モデルを作る機能ではありません。信頼できるモデルだけを指定してください。");
        _panel.Children.Add(_runtimeStatus);
        AddButton("補完環境を確認", CheckRuntimeAsync);
        AddText("参照する観測会話（同じ話者の、2～14秒の明瞭な区間）");
        _panel.Children.Add(_reference);
        _panel.Children.Add(_needs);
        _reference.SelectionChanged += (_, _) => UpdateNeeds();
        AddText("生成モデルのフォルダ（config.json・style_vectors.npy・safetensors 1件）");
        _panel.Children.Add(_modelPath); AddButton("生成モデルを選ぶ", SelectModelAsync);
        AddText("モデル内の話者名"); _panel.Children.Add(_modelSpeaker);
        AddText("未観測または少量（3回未満）の音素を含む日本語文章（120文字以内）"); _panel.Children.Add(_text);
        AddButton("補完候補を生成・検証する", GenerateAsync);
        AddText("候補一覧。機械検証の通過だけでは学習に追加しません。");
        _panel.Children.Add(_candidates); _panel.Children.Add(_details);
        _candidates.SelectionChanged += (_, _) =>
        {
            _reviewed.IsChecked = false;
            if (_candidates.SelectedItem is not CandidateRow row) { _details.Text = "候補を選択してください。"; return; }
            var v = row.Candidate.Verification;
            var targets = row.Candidate.TargetPhonemes.Count > 0
                ? row.Candidate.TargetPhonemes
                : v.MissingPhonemes;
            _details.Text = $"対象: {row.Candidate.SpeakerId} / 文章: {row.Candidate.Text}\n" +
                $"補助対象音素: {string.Join(" ", targets)}\n" +
                $"元の観測回数: {string.Join(", ", row.Candidate.ObservedCountByPhoneme.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"))}\n" +
                $"未観測音素: {string.Join(" ", v.MissingPhonemes)}\n再認識: {v.RecognizedText}\n" +
                $"話者類似度: {v.SpeakerSimilarity:0.000}（本人の確率ではありません）\n" + string.Join("\n", v.Reasons);
        };
        AddButton("選択した候補を試聴", async _ =>
        {
            var candidate = await RequireSelectedAsync();
            System.Diagnostics.Process.Start(new ProcessStartInfo(Path.Combine(_workspace.RootPath, candidate.AudioPath)) { UseShellExecute = true });
        });
        _panel.Children.Add(_reviewed);
        AddText("採用は話者ごとに1文章までです。別候補を採用すると切り替わります。未観測・少量のどちらも補完ONのトーク学習だけに追加し、録音済み音素の回数は増やしません。");
        AddButton("確認した候補をトーク学習へ採用", async token =>
        {
            if (_reviewed.IsChecked != true) throw new InvalidOperationException("先に試聴して、発音と話者を確認してください。");
            var candidate = await RequireSelectedAsync();
            await _service.SetAcceptedAsync(_workspace, candidate.CandidateId, true, token);
            _status.Text = "採用しました。補完ONで学習ジョブを新しく作成してください。実学習は開始していません。";
        });
        AddButton("選択した候補の採用を解除", async token =>
        {
            var candidate = await RequireSelectedAsync();
            await _service.SetAcceptedAsync(_workspace, candidate.CandidateId, false, token);
            _status.Text = "この候補の採用を解除しました。元の録音と候補ファイルは残しています。";
        });
        _panel.Children.Add(_progress); _panel.Children.Add(_status);
        Opened += async (_, _) => await RunAsync(async token =>
        {
            await CheckRuntimeAsync(token);
            var snapshot = await new TrainingDatasetSnapshotService(new UniversalVoiceDatasetRepository()).LoadAsync(_workspace, token);
            _snapshot = snapshot;
            _reference.ItemsSource = snapshot.Segments.Where(x => x.SpeakerId is not null &&
                    x.ContentType == SegmentContentType.Speech && x.AsrConfidence >= 0.65 && x.AlignmentConfidence >= 0.65)
                .Select(x => new ReferenceRow(x)).ToArray();
            await RefreshAsync(token);
        });
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
    }

    private async Task SelectModelAsync(CancellationToken token)
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "学習済み話者モデルを選択", AllowMultiple = false });
        token.ThrowIfCancellationRequested();
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "config.json"), token));
        var names = config.RootElement.GetProperty("data").GetProperty("spk2id").EnumerateObject().Select(x => x.Name).ToArray();
        _modelPath.Text = path; _modelSpeaker.ItemsSource = names;
        _modelSpeaker.SelectedIndex = names.Length == 1 ? 0 : -1;
    }

    private async Task GenerateAsync(CancellationToken token)
    {
        var runtime = await _service.CheckRuntimeAsync(false, token);
        if (!runtime.Ready)
            throw new InvalidOperationException(
                "音素補完に必要なワーカー資源が揃っていません。\n" +
                string.Join("\n", runtime.Issues));

        if (_reference.SelectedItem is not ReferenceRow source || _modelSpeaker.SelectedItem is not string modelSpeaker ||
            string.IsNullOrWhiteSpace(_modelPath.Text))
            throw new InvalidOperationException("参照区間・生成モデル・モデル内の話者を選択してください。");
        // UIプロパティはUIスレッドで取り出してから処理層へ渡す。
        var modelDirectory = _modelPath.Text;
        var text = _text.Text ?? "";
        var speakerId = source.Segment.SpeakerId!;
        var segmentId = source.Segment.SegmentId;
        _status.Text = "補完候補を生成し、再認識・話者・波形を検証しています…";
        var candidate = await Task.Run(() => _service.GenerateAsync(_workspace, speakerId,
            segmentId, modelDirectory, modelSpeaker, text, token), token);
        token.ThrowIfCancellationRequested();
        await RefreshAsync(token);
        _candidates.SelectedItem = ((IEnumerable<CandidateRow>)_candidates.ItemsSource!).First(x => x.Candidate.CandidateId == candidate.CandidateId);
        _status.Text = candidate.Verification.MachinePassed ? "機械検証を通過しました。試聴後に採用を判断してください。" : "検証を通過しませんでした。この候補は学習に採用できません。";
    }

    private async Task CheckRuntimeAsync(CancellationToken token)
    {
        var runtime = await _service.CheckRuntimeAsync(false, token);
        _runtimeStatus.Text = runtime.Ready
            ? "補完環境: 準備完了（固定モデル資源・辞書・runtime版を確認済み）"
            : "補完環境: 未準備\n" + string.Join("\n", runtime.Issues);
    }

    private void UpdateNeeds()
    {
        if (_snapshot is null || _reference.SelectedItem is not ReferenceRow row || row.Segment.SpeakerId is not { } speakerId)
        {
            _needs.Text = "参照区間を選ぶと補助対象音素を表示します。";
            return;
        }
        var counts = PhonemeSupplementService.ReliablePhonemeCounts(_snapshot, speakerId);
        var targets = PhonemeSupplementService.SupplementTargets(_snapshot, speakerId);
        var missing = targets.Where(x => counts.GetValueOrDefault(x) == 0).ToArray();
        var sparse = targets.Where(x => counts.GetValueOrDefault(x) is > 0 and < PhonemeSupplementService.MinimumObservedCount)
            .Select(x => $"{x}({counts[x]}回)").ToArray();
        _needs.Text = $"未観測: {(missing.Length == 0 ? "なし" : string.Join(" ", missing))}\n" +
            $"少量: {(sparse.Length == 0 ? "なし" : string.Join(" ", sparse))}";
    }

    private async Task RefreshAsync(CancellationToken token) =>
        _candidates.ItemsSource = (await _service.ListAsync(_workspace, token)).Select(x => new CandidateRow(x)).ToArray();
    private Task<PhonemeSupplementCandidate> RequireSelectedAsync() => _candidates.SelectedItem is CandidateRow row
        ? _service.LoadAsync(_workspace, row.Candidate.CandidateId, _lifetime.Token)
        : throw new InvalidOperationException("補完候補を選択してください。");
    private void AddText(string text) => _panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
    private void AddButton(string text, Func<CancellationToken, Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await RunAsync(action);
        _panel.Children.Add(button);
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _closed) return;
        _busy = true; _progress.IsVisible = true;
        foreach (var control in _panel.Children) if (control != _status && control != _progress) control.IsEnabled = false;
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) _status.Text = "処理できませんでした。" + ex.Message; }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                _progress.IsVisible = false;
                foreach (var control in _panel.Children) control.IsEnabled = true;
            }
            else _lifetime.Dispose();
        }
    }
    private sealed record ReferenceRow(UniversalVoiceSegment Segment)
    {
        public override string ToString() => $"{Segment.SpeakerId} / {Segment.SegmentId} / {Segment.Transcript}";
    }
    private sealed record CandidateRow(PhonemeSupplementCandidate Candidate)
    {
        public override string ToString() => $"{Candidate.SpeakerId} / {(Candidate.Verification.MachinePassed ? "機械検証通過" : "採用不可")} / {Candidate.Text}";
    }
}
