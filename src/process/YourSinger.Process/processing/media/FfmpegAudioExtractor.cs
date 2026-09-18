using System.Diagnostics;

namespace YourSinger.Process.Processing.Media;

public sealed class FfmpegAudioExtractor
{
    private readonly string _ffmpegPath;

    public FfmpegAudioExtractor(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? ResolveDefaultPath();
    }

    public async Task ExtractAsync(string sourcePath, string outputWavPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputWavPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath,
            "-vn", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", outputWavPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                "FFmpegを起動できません。配布物の tools/ffmpeg を確認してください。",
                exception);
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"音声の抽出に失敗しました。FFmpeg終了コード: {process.ExitCode}\n{error}");
        }
    }

    private static string ResolveDefaultPath()
    {
        var executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", executableName);
        return File.Exists(bundledPath) ? bundledPath : executableName;
    }
}
