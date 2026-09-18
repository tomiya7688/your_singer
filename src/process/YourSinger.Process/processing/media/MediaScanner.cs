using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Media;

public sealed class MediaScanner
{
    private readonly SourceFingerprintService _fingerprintService;

    public MediaScanner(SourceFingerprintService fingerprintService)
    {
        _fingerprintService = fingerprintService;
    }

    public async Task<IReadOnlyList<SourceRecord>> ScanAsync(
        string inputPath,
        IReadOnlyCollection<SourceRecord> previousSources,
        string analysisVersion,
        IProgress<MediaScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = EnumerateSupportedFiles(inputPath).ToArray();
        var previousByPath = previousSources.ToDictionary(
            source => NormalizePath(source.Path),
            StringComparer.OrdinalIgnoreCase);

        var result = new List<SourceRecord>(files.Length);

        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = files[index];
            var position = index + 1;

            progress?.Report(new(
                MediaScanProgressKind.Hashing,
                path,
                position,
                files.Length,
                "ファイル内容を確認しています"));

            try
            {
                var info = new FileInfo(path);
                var hash = await _fingerprintService.ComputeSha256Async(path, cancellationToken);
                previousByPath.TryGetValue(NormalizePath(path), out var previous);

                var unchanged =
                    previous is not null &&
                    previous.Size == info.Length &&
                    previous.ModifiedTime.UtcDateTime == info.LastWriteTimeUtc &&
                    string.Equals(previous.ContentHash, hash, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.AnalysisVersion, analysisVersion, StringComparison.Ordinal);

                if (unchanged)
                {
                    result.Add(previous!);
                    progress?.Report(new(
                        MediaScanProgressKind.Unchanged,
                        path,
                        position,
                        files.Length,
                        "変更なし: 再解析を省略します"));
                    continue;
                }

                _ = SupportedMedia.TryGetMediaType(path, out var mediaType);

                var source = new SourceRecord
                {
                    SourceId = previous?.SourceId ?? $"src_{Guid.NewGuid():N}",
                    Path = Path.GetFullPath(path),
                    MediaType = mediaType,
                    Size = info.Length,
                    ModifiedTime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    ContentHash = hash,
                    AnalysisVersion = analysisVersion,
                    ExtractedAudioPath = null,
                    AnalysisState = SourceAnalysisState.Pending
                };

                result.Add(source);

                progress?.Report(new(
                    previous is null ? MediaScanProgressKind.Added : MediaScanProgressKind.Changed,
                    path,
                    position,
                    files.Length,
                    previous is null ? "新しい素材を登録しました" : "変更された素材を再解析対象にしました"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                progress?.Report(new(
                    MediaScanProgressKind.Failed,
                    path,
                    position,
                    files.Length,
                    $"読み込みをスキップしました: {exception.Message}"));
            }
        }

        progress?.Report(new(
            MediaScanProgressKind.Completed,
            inputPath,
            files.Length,
            files.Length,
            $"{files.Length}件の対応素材を確認しました"));

        return result;
    }

    private static IEnumerable<string> EnumerateSupportedFiles(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            if (SupportedMedia.TryGetMediaType(inputPath, out _))
            {
                yield return Path.GetFullPath(inputPath);
            }

            yield break;
        }

        if (!Directory.Exists(inputPath))
        {
            throw new DirectoryNotFoundException($"入力先が見つかりません: {inputPath}");
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(inputPath));

        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> files;
            IEnumerable<string> directories;

            try
            {
                files = Directory.EnumerateFiles(directory).ToArray();
                directories = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (SupportedMedia.TryGetMediaType(file, out _))
                {
                    yield return file;
                }
            }

            foreach (var child in directories)
            {
                pending.Push(child);
            }
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
