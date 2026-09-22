using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.App;

public sealed partial class TrainingHistoryWindow : Window
{
    private readonly TrainingJobRepository _jobs = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ProjectWorkspace? _workspace;
    private bool _busy;
    private bool _closed;

    public string? LastRecreatedBatchId { get; private set; }

    public TrainingHistoryWindow() : this(null) { }

    public TrainingHistoryWindow(ProjectWorkspace? workspace)
    {
        InitializeComponent();
        _workspace = workspace;
        Opened += async (_, _) => await RunAsync(async token =>
        {
            if (_workspace is not null) await LoadProjectAsync(_workspace, token);
        });
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private async void OnOpenProjectClick(object? sender, RoutedEventArgs e) =>
        await RunAsync(async token =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "再作成するプロジェクトの project.json を選択",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("プロジェクト設定") { Patterns = ["project.json"] }]
            });
            token.ThrowIfCancellationRequested();
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            if (!string.Equals(Path.GetFileName(path), "project.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("プロジェクト直下の project.json を選択してください。");
            await LoadProjectAsync(new ProjectWorkspace(Path.GetDirectoryName(path)!), token);
        });

    private async Task LoadProjectAsync(ProjectWorkspace workspace, CancellationToken token)
    {
        var project = await new ProjectRepository().LoadAsync(workspace, token)
            ?? throw new InvalidOperationException("プロジェクト設定が見つかりません。");
        if (project.SchemaVersion != ProjectManifest.CurrentSchemaVersion)
            throw new InvalidOperationException("このプロジェクト形式には対応していません。対応するアプリで開いてください。");
        // 読み込みに失敗した別プロジェクトへ、現在の履歴を切り替えない。
        var history = await _jobs.ListBatchesAsync(workspace, token);
        _workspace = workspace;
        HistoryProjectText.Text = $"{project.Name}\n{workspace.RootPath}";
        ApplyHistory(history, null);
    }

    private async void OnRefreshHistoryClick(object? sender, RoutedEventArgs e) =>
        await RunAsync(token => RefreshHistoryAsync(null, token));

    private async Task RefreshHistoryAsync(string? selectId, CancellationToken token)
    {
        if (_workspace is null) return;
        var history = await _jobs.ListBatchesAsync(_workspace, token);
        ApplyHistory(history, selectId ?? (BatchList.SelectedItem as BatchRow)?.Batch.BatchId);
    }

    private void ApplyHistory(TrainingBatchHistory history, string? selectId)
    {
        var rows = history.Batches.Select(x => new BatchRow(x)).ToArray();
        BatchList.ItemsSource = rows;
        BatchList.SelectedItem = rows.FirstOrDefault(x => x.Batch.BatchId == selectId);
        var legacy = history.Batches.Count(TrainingJobPlanningService.RequiresRecreation);
        var message = legacy > 0
            ? $"旧形式のジョブ一式が {legacy}件あります。必要なものを選択して再作成してください。"
            : "現行形式の履歴です。素材や設定との一致は学習入力の作成時にも確認します。";
        if (history.Errors.Count > 0)
            message += "\n" + string.Join("\n", history.Errors.Select(x => $"{x.FileName}: {x.Message}"));
        HistoryWarningText.Text = message;
        HistoryStatusText.Text = rows.Length == 0 ? "読み込める学習履歴はありません。" : $"履歴 {rows.Length}件を読み込みました。";
        UpdateSelection();
    }

    private void OnBatchSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (BatchList.SelectedItem is not BatchRow row)
        {
            BatchDetailText.Text = "再作成するジョブ一式を選択してください。";
            RecreateBatchButton.IsEnabled = false;
            return;
        }
        var batch = row.Batch;
        BatchDetailText.Text = $"識別子: {batch.BatchId}\n入力形式: {(string.IsNullOrEmpty(batch.InputVersion) ? "旧形式（版情報なし）" : batch.InputVersion)}\n" +
            (batch.RecreatedFromBatchId is null ? "" : $"再作成元: {batch.RecreatedFromBatchId}\n") +
            string.Join("\n", batch.Jobs.Select(job =>
                $"{job.SpeakerId} / {TargetLabel(job.Target)} / 補完{(job.AutoCorrectionEnabled ? "ON" : "OFF")} / {StateLabel(job.State)} / {job.SegmentIds.Count}区間"));
        var running = batch.Jobs.Any(job => job.State == TrainingJobState.Running);
        if (running) BatchDetailText.Text += "\n実行中のジョブを含むため、終了または中止を確認するまで再作成できません。";
        RecreateBatchButton.IsEnabled = !_busy && !running;
    }

    private async void OnRecreateBatchClick(object? sender, RoutedEventArgs e)
    {
        if (_workspace is not { } workspace || BatchList.SelectedItem is not BatchRow row) return;
        await RunAsync(async token =>
        {
            if (!await ConfirmAsync(row.Batch))
            {
                HistoryStatusText.Text = "再作成を取り消しました。保存データは変更していません。";
                return;
            }
            token.ThrowIfCancellationRequested();
            HistoryStatusText.Text = "現在の保存データから学習ジョブを再作成しています…";
            var dataset = new UniversalVoiceDatasetRepository();
            var planner = new TrainingJobPlanningService(dataset,
                new AutoCorrectionService(dataset, new AutoCorrectionRepository()), _jobs);
            var created = await Task.Run(() => planner.RecreateBatchAsync(workspace, row.Batch.BatchId, token), token);
            LastRecreatedBatchId = created.BatchId;
            // 保存成功後の表示失敗を、再作成そのものの失敗として再試行させない。
            try
            {
                await RefreshHistoryAsync(created.BatchId, token);
                HistoryStatusText.Text = $"{created.Jobs.Count}件の新しいジョブを作成しました。学習は開始していません。\n{created.BatchId}";
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                HistoryStatusText.Text = $"ジョブ作成は完了しましたが、一覧を更新できませんでした。「一覧を更新」で確認してください。\n{created.BatchId}";
            }
        });
    }

    private async Task<bool> ConfirmAsync(TrainingJobBatch batch)
    {
        var dialog = new Window
        {
            Title = "学習ジョブを再作成しますか？", Width = 580,
            SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "取り消す", IsCancel = true };
        var confirm = new Button { Content = "元の記録を残して再作成" };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = $"{batch.BatchId}\n\n元の話者・用途・補完ON/OFFを引き継ぎます。現在の手動修正・除外と、現在の音高補完の詳細設定を適用し、新しいジョブを作成します。\n\n元のジョブ・ログ・重みは削除しません。音声の再解析や学習は開始しません。",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { cancel, confirm }
                }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _closed) return;
        _busy = true;
        SetControlsEnabled(false);
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception)
        {
            if (!_closed) HistoryStatusText.Text = exception switch
            {
                System.Text.Json.JsonException => "保存データを読み込めません。プロジェクト設定を確認してください。",
                UnauthorizedAccessException => "保存先の読み取り・書き込み権限を確認してください。",
                FileNotFoundException => "必要な保存データまたは特徴ファイルがありません。プロジェクトを確認してください。",
                _ => $"処理できませんでした。{exception.Message}"
            };
        }
        finally
        {
            _busy = false;
            if (!_closed) SetControlsEnabled(true);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        OpenHistoryProjectButton.IsEnabled = enabled;
        RefreshHistoryButton.IsEnabled = enabled && _workspace is not null;
        BatchList.IsEnabled = enabled;
        UpdateSelection();
    }

    private static string TargetLabel(TrainingTarget target) => target switch
    {
        TrainingTarget.Talk => "トーク", TrainingTarget.Singing => "歌唱", _ => "両方"
    };

    private static string StateLabel(TrainingJobState state) => state switch
    {
        TrainingJobState.Pending => "待機中", TrainingJobState.Running => "実行中",
        TrainingJobState.Succeeded => "完了", TrainingJobState.Failed => "失敗",
        TrainingJobState.Canceled => "中止", _ => "不明"
    };

    private sealed record BatchRow(TrainingJobBatch Batch)
    {
        public override string ToString() =>
            $"{Batch.CreatedAt.ToLocalTime():yyyy/MM/dd HH:mm} / {Batch.Jobs.Count}ジョブ / " +
            (TrainingJobPlanningService.RequiresRecreation(Batch) ? "旧形式・再作成が必要" : "現行形式") +
            $" / {Batch.BatchId}";
    }
}
