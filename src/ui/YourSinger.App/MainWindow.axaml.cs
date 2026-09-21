using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Audio;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Media;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Project;
using YourSinger.Process.Processing.Speaker;
using YourSinger.Process.Processing.Training;

namespace YourSinger.App;

public sealed partial class MainWindow : Window
{
    private readonly ProjectRepository _projectRepository = new();
    private readonly SpeakerAnalysisRepository _speakerRepository = new();
    private readonly ContentClassificationRepository _classificationRepository = new();
    private readonly UniversalVoiceDatasetRepository _datasetRepository = new();
    private readonly PhonemeCoverageRepository _coverageRepository = new();
    private readonly SingingQualityRepository _singingQualityRepository = new();
    private readonly TrainingJobRepository _trainingJobRepository = new();
    private readonly AutoCorrectionRepository _autoCorrectionRepository = new();

    private readonly Dictionary<string, CheckBox> _speakerChecks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComboBox> _speakerTargets = new(StringComparer.Ordinal);

    private ProjectWorkspace? _workspace;
    private ProjectManifest? _manifest;
    private Func<Task>? _retryAction;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnSelectFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "素材フォルダを選択",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.TryGetLocalPath() is { } path)
            await ImportAsync(path, Path.GetFileName(path));
    }

    private async void OnSelectFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "動画・音声ファイルを選択",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("動画・音声")
                {
                    Patterns =
                    [
                        "*.wav", "*.flac", "*.mp3", "*.m4a", "*.ogg", "*.opus",
                        "*.mp4", "*.mkv", "*.webm", "*.mov", "*.avi", "*.m4v"
                    ]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file?.TryGetLocalPath() is { } path)
            await ImportAsync(path, Path.GetFileNameWithoutExtension(path));
    }

    private async Task ImportAsync(string inputPath, string projectName)
    {
        await RunUiActionAsync(async () =>
        {
            FolderButton.IsEnabled = false;
            FileButton.IsEnabled = false;
            ProgressBar.Value = 0;

            var workspacePath = CreateWorkspacePath(projectName);
            _workspace = new ProjectWorkspace(workspacePath);

            var importService = new ProjectImportService(
                _projectRepository,
                new MediaScanner(new SourceFingerprintService()),
                new FfmpegAudioExtractor());

            _manifest = await importService.CreateOrUpdateAsync(
                workspacePath,
                projectName,
                inputPath,
                new Progress<MediaScanProgress>(OnProgress));

            ProjectPathText.Text = workspacePath;
            AnalyzeButton.IsEnabled = true;
            CreateJobsButton.IsEnabled = true;
            ProgressBar.Value = 1;
            await RefreshAsync();
        }, "素材の読み込みに失敗しました。");
    }

    private async void OnAnalyzeClick(object? sender, RoutedEventArgs e)
    {
        await AnalyzeAsync();
    }

    private async Task AnalyzeAsync()
    {
        if (_workspace is null || _manifest is null)
            return;

        await RunUiActionAsync(async () =>
        {
            var worker = new MlWorkerClient();
            var preprocessingRepository = new AudioPreprocessingRepository();

            var preprocessing = new AudioPreprocessingService(worker, preprocessingRepository);
            var readySources = _manifest.Sources
                .Where(x => x.AnalysisState == SourceAnalysisState.Ready)
                .ToArray();

            for (var index = 0; index < readySources.Length; index++)
            {
                var source = readySources[index];
                StatusText.Text = $"音声前処理 {index + 1}/{readySources.Length}: {Path.GetFileName(source.Path)}";
                await preprocessing.ProcessAsync(
                    _workspace,
                    source,
                    new Progress<AudioPreprocessingProgress>(p =>
                    {
                        ProgressBar.Value = readySources.Length == 0
                            ? 0
                            : Math.Clamp((index + p.Progress) / readySources.Length, 0, 1);
                        StatusText.Text = p.Message;
                    }));
            }

            StatusText.Text = "話者を解析しています…";
            await new SpeakerAnalysisService(
                worker,
                preprocessingRepository,
                _speakerRepository)
                .AnalyzeProjectAsync(_workspace, readySources);

            StatusText.Text = "Speech / Singingを分類しています…";
            await new ContentClassificationService(
                worker,
                preprocessingRepository,
                _classificationRepository)
                .ClassifyProjectAsync(_workspace, readySources);

            StatusText.Text = "共通特徴を抽出しています…";
            await new UniversalVoiceDatasetService(
                worker,
                preprocessingRepository,
                _speakerRepository,
                _classificationRepository,
                _datasetRepository,
                new FeatureCacheKeyService())
                .BuildAsync(_workspace, readySources);

            StatusText.Text = "品質を診断しています…";
            await new PhonemeCoverageDiagnosticService(
                _datasetRepository,
                _coverageRepository)
                .DiagnoseAsync(_workspace);

            await new SingingQualityDiagnosticService(
                _datasetRepository,
                _singingQualityRepository)
                .DiagnoseAsync(_workspace);

            ProgressBar.Value = 1;
            await RefreshAsync();
            StatusText.Text = "解析と品質診断が完了しました。";
        }, "解析に失敗しました。", AnalyzeAsync);
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_workspace is null)
            return;

        _manifest ??= await _projectRepository.LoadAsync(_workspace);
        if (_manifest is not null)
        {
            var ready = _manifest.Sources.Count(x => x.AnalysisState == SourceAnalysisState.Ready);
            var failed = _manifest.Sources.Count - ready;
            SummaryText.Text = $"素材 {_manifest.Sources.Count}件 / 準備完了 {ready}件 / 失敗 {failed}件";
            SourceList.ItemsSource = _manifest.Sources.Select(source =>
                $"{(source.AnalysisState == SourceAnalysisState.Ready ? "✓" : "×")} " +
                $"{Path.GetFileName(source.Path)}  [{ToJapaneseMediaType(source.MediaType)}]").ToArray();
        }

        await RefreshSpeakersAsync();
        await RefreshClassificationsAsync();
        await RefreshQualityAsync();
        RefreshTraining();
    }

    private async Task RefreshSpeakersAsync()
    {
        SpeakerPanel.Children.Clear();
        _speakerChecks.Clear();
        _speakerTargets.Clear();

        if (_workspace is null)
            return;

        var record = await _speakerRepository.LoadAsync(_workspace);
        if (record is null)
        {
            SpeakerPanel.Children.Add(new TextBlock { Text = "話者解析結果はまだありません。" });
            return;
        }

        foreach (var speaker in record.Speakers)
        {
            var check = new CheckBox
            {
                Content = $"{speaker.DisplayName}  使用可能 {speaker.UsableDurationSec:0.0}秒",
                IsChecked = speaker.UserSelected
            };
            var target = new ComboBox
            {
                ItemsSource = new[] { "両方", "歌唱", "トーク" },
                SelectedIndex = 0,
                Width = 120
            };
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(check);
            row.Children.Add(target);
            SpeakerPanel.Children.Add(row);
            _speakerChecks[speaker.SpeakerId] = check;
            _speakerTargets[speaker.SpeakerId] = target;
        }
    }

    private async Task RefreshClassificationsAsync()
    {
        if (_workspace is null)
            return;

        var record = await _classificationRepository.LoadAsync(_workspace);
        ClassificationList.ItemsSource = record?.Classifications
            .Select(x => new ClassificationUiItem(
                x.SegmentId,
                x.ContentType,
                x.Confidence,
                x.UserOverridden))
            .ToArray() ?? [];
    }

    private async Task RefreshQualityAsync()
    {
        if (_workspace is null)
            return;

        var coverage = await _coverageRepository.LoadAsync(_workspace);
        var globalCoverage = coverage?.Scopes.FirstOrDefault(x =>
            x.SpeakerId is null && x.ContentType is null);

        CoverageSummaryText.Text = globalCoverage is null
            ? "診断結果はまだありません。"
            : $"品質: {CoverageLabel(globalCoverage.Quality)} / 不足 {globalCoverage.MissingPhonemes.Count} / 少量 {globalCoverage.LowCountPhonemes.Count}";

        CoverageList.ItemsSource = globalCoverage?.Categories
            .Select(x => $"{CoverageLabel(x.Quality)}  {x.Category}: {x.Observed}/{x.Expected} ({x.Ratio:P0})")
            .ToArray() ?? [];

        var singing = await _singingQualityRepository.LoadAsync(_workspace);
        var globalSinging = singing?.Scopes.FirstOrDefault(x => x.SpeakerId is null);
        SingingSummaryText.Text = globalSinging is null
            ? "診断結果はまだありません。"
            : $"品質: {SingingLabel(globalSinging.Quality)} / 音域 {globalSinging.MinF0Hz:0}-{globalSinging.MaxF0Hz:0}Hz / pitch信頼度 {globalSinging.MeanPitchReliability:P0}";

        SingingQualityList.ItemsSource = globalSinging?.Warnings.Count > 0
            ? globalSinging.Warnings.Select(x => $"注意: {x}").ToArray()
            : globalSinging is null ? [] : ["良好: 主要な歌唱品質警告はありません。"];
    }

    private void RefreshTraining()
    {
        if (_workspace is null)
            return;

        var directory = Path.Combine(_workspace.MetadataPath, "training-jobs");
        if (!Directory.Exists(directory))
        {
            TrainingList.ItemsSource = Array.Empty<string>();
            return;
        }

        TrainingList.ItemsSource = Directory.GetFiles(directory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToArray();
    }

    private async void OnMergeSpeakersClick(object? sender, RoutedEventArgs e)
    {
        if (_workspace is null)
            return;

        var selected = _speakerChecks
            .Where(x => x.Value.IsChecked == true)
            .Select(x => x.Key)
            .ToArray();

        if (selected.Length < 2)
        {
            StatusText.Text = "統合する話者を2人以上選択してください。";
            return;
        }

        await RunUiActionAsync(async () =>
        {
            await new SpeakerOverrideService(_speakerRepository)
                .MergeSpeakersAsync(_workspace, selected[0], selected.Skip(1).ToArray());
            await RefreshSpeakersAsync();
            StatusText.Text = "選択した話者クラスタを統合しました。";
        }, "話者クラスタの統合に失敗しました。");
    }

    private async void OnOpenRepresentativeClick(object? sender, RoutedEventArgs e)
    {
        if (_workspace is null)
            return;

        var speakerId = _speakerChecks.FirstOrDefault(x => x.Value.IsChecked == true).Key;
        if (speakerId is null)
        {
            StatusText.Text = "代表サンプルを確認する話者を選択してください。";
            return;
        }

        var speakers = await _speakerRepository.LoadAsync(_workspace);
        var representativeId = speakers?.Speakers
            .FirstOrDefault(x => x.SpeakerId == speakerId)?
            .RepresentativeSegmentIds.FirstOrDefault();

        var dataset = await _datasetRepository.LoadAsync(_workspace);
        var path = dataset?.Segments
            .FirstOrDefault(x => x.SegmentId == representativeId)?
            .AudioPath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText.Text = "代表サンプル音声が見つかりません。";
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void OnSetSpeechClick(object? sender, RoutedEventArgs e) =>
        await SetSelectedClassificationAsync(SegmentContentType.Speech);

    private async void OnSetSingingClick(object? sender, RoutedEventArgs e) =>
        await SetSelectedClassificationAsync(SegmentContentType.Singing);

    private async Task SetSelectedClassificationAsync(SegmentContentType type)
    {
        if (_workspace is null || ClassificationList.SelectedItem is not ClassificationUiItem item)
            return;

        await new ContentClassificationOverrideService(_classificationRepository)
            .SetContentTypeAsync(_workspace, item.SegmentId, type);
        await RefreshClassificationsAsync();
        StatusText.Text = $"分類を{(type == SegmentContentType.Speech ? "Speech" : "Singing")}へ修正しました。";
    }

    private async void OnExcludeSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (_workspace is null || ClassificationList.SelectedItem is not ClassificationUiItem item)
            return;

        await new SpeakerOverrideService(_speakerRepository)
            .ExcludeSegmentAsync(_workspace, item.SegmentId, true);
        StatusText.Text = "選択セグメントを学習対象から除外しました。";
        await RefreshSpeakersAsync();
    }

    private async void OnCreateJobsClick(object? sender, RoutedEventArgs e)
    {
        if (_workspace is null)
            return;

        var selections = _speakerChecks
            .Where(x => x.Value.IsChecked == true)
            .Select(x => new SpeakerTrainingSelection
            {
                SpeakerId = x.Key,
                Target = TargetFromIndex(_speakerTargets[x.Key].SelectedIndex),
                AutoCorrectionEnabled = AutoCorrectionCheckBox.IsChecked != false
            })
            .ToArray();

        await RunUiActionAsync(async () =>
        {
            var planner = new TrainingJobPlanningService(
                _datasetRepository,
                new AutoCorrectionService(_datasetRepository, _autoCorrectionRepository),
                _trainingJobRepository);

            var batch = await planner.CreateBatchAsync(_workspace, selections);
            TrainingList.ItemsSource = batch.Jobs.Select(job =>
                $"{job.SpeakerId} / {TargetLabel(job.Target)} / {job.State} / {job.SegmentIds.Count} segments").ToArray();
            StatusText.Text = $"学習ジョブ {batch.Jobs.Count}件を作成しました。";
        }, "学習ジョブの作成に失敗しました。");
    }

    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        if (_retryAction is not null)
            await _retryAction();
    }

    private async Task RunUiActionAsync(
        Func<Task> action,
        string errorPrefix,
        Func<Task>? retryAction = null)
    {
        RetryButton.IsVisible = false;
        _retryAction = null;

        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"{errorPrefix} {exception.Message}";
            _retryAction = retryAction ?? action;
            RetryButton.IsVisible = true;
        }
        finally
        {
            FolderButton.IsEnabled = true;
            FileButton.IsEnabled = true;
        }
    }

    private void OnProgress(MediaScanProgress progress)
    {
        StatusText.Text = progress.Message;
        if (progress.Total > 0)
            ProgressBar.Value = Math.Clamp((double)progress.Processed / progress.Total, 0, 1);
    }

    private static TrainingTarget TargetFromIndex(int index) => index switch
    {
        1 => TrainingTarget.Singing,
        2 => TrainingTarget.Talk,
        _ => TrainingTarget.Both
    };

    private static string TargetLabel(TrainingTarget target) => target switch
    {
        TrainingTarget.Singing => "歌唱",
        TrainingTarget.Talk => "トーク",
        _ => "両方"
    };

    private static string CoverageLabel(CoverageQuality quality) => quality switch
    {
        CoverageQuality.Good => "良好",
        CoverageQuality.Warning => "注意",
        _ => "不足"
    };

    private static string SingingLabel(SingingQualityLevel quality) => quality switch
    {
        SingingQualityLevel.Good => "良好",
        SingingQualityLevel.Warning => "注意",
        _ => "不足"
    };

    private static string ToJapaneseMediaType(MediaType mediaType) =>
        mediaType == MediaType.Audio ? "音声" : "動画";

    private static string CreateWorkspacePath(string projectName)
    {
        var safeName = string.Concat(projectName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YourSinger",
            "projects");

        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}");
    }

    private sealed record ClassificationUiItem(
        string SegmentId,
        SegmentContentType Type,
        double Confidence,
        bool UserOverridden)
    {
        public override string ToString() =>
            $"{(UserOverridden ? "手動" : "自動")}  {Type}  {Confidence:P0}  {SegmentId}";
    }
}
