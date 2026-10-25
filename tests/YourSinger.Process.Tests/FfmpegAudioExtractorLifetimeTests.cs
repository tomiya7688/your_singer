using System.Diagnostics;
using YourSinger.Process.Processing.Media;

namespace YourSinger.Process.Tests;

public sealed class FfmpegAudioExtractorLifetimeTests
{
    [Fact]
    public async Task CancellationReapsFfmpegProcess()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(
            Path.GetTempPath(),
            "YourSinger.Tests",
            $"ffmpeg-lifetime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "fake-ffmpeg");
        var pidPath = Path.Combine(root, "child.pid");
        var outputPath = Path.Combine(root, "output.wav");
        int? pid = null;

        try
        {
            await File.WriteAllTextAsync(
                executable,
                """
                #!/usr/bin/env python3
                import os
                import sys
                import time

                source = sys.argv[sys.argv.index("-i") + 1]
                with open(source, "w", encoding="utf-8") as stream:
                    stream.write(str(os.getpid()))
                    stream.flush()
                    os.fsync(stream.fileno())
                time.sleep(30)
                """,
                TestProject.Token);
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using var cancellation = new CancellationTokenSource();
            var extraction = new FfmpegAudioExtractor(executable)
                .ExtractAsync(pidPath, outputPath, cancellation.Token);

            for (var attempt = 0; attempt < 100 && !File.Exists(pidPath); attempt++)
                await Task.Delay(50, TestProject.Token);

            Assert.True(File.Exists(pidPath), "fake FFmpeg did not publish its PID");
            pid = int.Parse(await File.ReadAllTextAsync(pidPath, TestProject.Token));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await extraction);

            Assert.False(
                Directory.Exists($"/proc/{pid}"),
                "ExtractAsync returned after cancellation while the FFmpeg child was still alive");
        }
        finally
        {
            if (pid is int childPid)
            {
                try
                {
                    using var child = Process.GetProcessById(childPid);
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync(CancellationToken.None);
                    }
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
