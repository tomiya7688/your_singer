using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Media;

namespace YourSinger.Process.Processing.Project;

public sealed class ProjectImportService
{
    public const string AnalysisVersion = "v1-01.1";

    private readonly ProjectRepository _repository;
    private readonly MediaScanner _scanner;
    private readonly FfmpegAudioExtractor _extractor;

    public ProjectImportService(ProjectRepository repository, MediaScanner scanner, FfmpegAudioExtractor extractor)
    {
        _repository = repository;
        _scanner = scanner;
        _extractor = extractor;
    }

    public async Task<ProjectManifest> CreateOrUpdateAsync(
        string workspacePath,
        string projectName,
        string inputPath,
        IProgress<MediaScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = new ProjectWorkspace(workspacePath);
        workspace.EnsureCreated();

        var manifest = await _repository.LoadAsync(workspace, cancellationToken)
            ?? new ProjectManifest
            {
                ProjectId = $"project_{Guid.NewGuid():N}",
                Name = projectName,
                AnalysisVersion = AnalysisVersion
            };

        manifest.Name = projectName;
        manifest.AnalysisVersion = AnalysisVersion;

        var normalizedInput = Path.GetFullPath(inputPath);
        if (!manifest.InputRoots.Contains(normalizedInput, StringComparer.OrdinalIgnoreCase))
        {
            manifest.InputRoots.Add(normalizedInput);
        }

        var scanned = await _scanner.ScanAsync(
            normalizedInput,
            manifest.Sources,
            AnalysisVersion,
            progress,
            cancellationToken);

        var oldById = manifest.Sources.ToDictionary(source => source.SourceId);
        manifest.Sources.Clear();
        manifest.Sources.AddRange(scanned);

        foreach (var source in manifest.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (oldById.TryGetValue(source.SourceId, out var old) &&
                old.ContentHash == source.ContentHash &&
                old.AnalysisVersion == source.AnalysisVersion &&
                old.AnalysisState == SourceAnalysisState.Ready &&
                old.ExtractedAudioPath is not null &&
                File.Exists(Path.Combine(workspace.RootPath, old.ExtractedAudioPath)))
            {
                source.ExtractedAudioPath = old.ExtractedAudioPath;
                source.AnalysisState = SourceAnalysisState.Ready;
                source.ErrorMessage = null;
                continue;
            }

            var relativeOutput = Path.Combine("extracted", $"{source.SourceId}.wav");
            var absoluteOutput = Path.Combine(workspace.RootPath, relativeOutput);

            progress?.Report(new(
                MediaScanProgressKind.Extracting,
                source.Path,
                0,
                manifest.Sources.Count,
                "学習用の音声へ変換しています"));

            try
            {
                await _extractor.ExtractAsync(source.Path, absoluteOutput, cancellationToken);
                source.ExtractedAudioPath = relativeOutput.Replace('\\', '/');
                source.AnalysisState = SourceAnalysisState.Ready;
                source.ErrorMessage = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                source.AnalysisState = SourceAnalysisState.Failed;
                source.ErrorMessage = exception.Message;

                progress?.Report(new(
                    MediaScanProgressKind.Failed,
                    source.Path,
                    0,
                    manifest.Sources.Count,
                    exception.Message));
            }

            await _repository.SaveAsync(workspace, manifest, cancellationToken);
        }

        await _repository.SaveAsync(workspace, manifest, cancellationToken);
        return manifest;
    }
}
