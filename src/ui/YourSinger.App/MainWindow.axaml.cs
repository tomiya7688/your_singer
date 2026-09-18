using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using YourSinger.Data.Models;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Media;
using YourSinger.Process.Processing.Project;

namespace YourSinger.App;

public sealed partial class MainWindow : Window
{
    private readonly ProjectImportService _importService;

    public MainWindow()
    {
        InitializeComponent();

        _importService = new ProjectImportService(
            new ProjectRepository(),
            new MediaScanner(new SourceFingerprintService()),
            new FfmpegAudioExtractor());
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
        {
            await ImportAsync(path, Path.GetFileName(path));
        }
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
        {
            await ImportAsync(path, Path.GetFileNameWithoutExtension(path));
        }
    }

    private async Task ImportAsync(string inputPath, string projectName)
    {
        FolderButton.IsEnabled = false;
        FileButton.IsEnabled = false;
        SourceList.ItemsSource = null;
        ProgressBar.Value = 0;

        var workspacePath = CreateWorkspacePath(projectName);
        var progress = new Progress<MediaScanProgress>(OnProgress);

        try
        {
            var manifest = await _importService.CreateOrUpdateAsync(
                workspacePath,
                projectName,
                inputPath,
                progress);

            var ready = manifest.Sources.Count(x => x.AnalysisState == SourceAnalysisState.Ready);
            var failed = manifest.Sources.Count - ready;

            SummaryText.Text =
                $"素材 {manifest.Sources.Count}件 / 準備完了 {ready}件 / 失敗 {failed}件";

            SourceList.ItemsSource = manifest.Sources.Select(source =>
                $"{(source.AnalysisState == SourceAnalysisState.Ready ? "✓" : "×")} " +
                $"{Path.GetFileName(source.Path)}  [{ToJapaneseMediaType(source.MediaType)}]");

            StatusText.Text = failed == 0
                ? "プロジェクトの作成が完了しました。"
                : "一部の素材を処理できませんでした。詳細は project.json に保存されています。";

            ProgressBar.Value = 1;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"処理に失敗しました: {exception.Message}";
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
        {
            ProgressBar.Value = Math.Clamp(
                (double)progress.Processed / progress.Total,
                0,
                1);
        }
    }

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
}
