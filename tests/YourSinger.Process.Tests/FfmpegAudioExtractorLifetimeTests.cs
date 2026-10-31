using YourSinger.Process.Processing.Media;

namespace YourSinger.Process.Tests;

public sealed class FfmpegAudioExtractorLifetimeTests
{
    [Fact]
    public async Task CancellationReapsFfmpegProcess()
    {
        // /procでプロセスの生存を調べるため、Linuxだけで実行する。他OSでは成功扱いにせずスキップとして報告する。
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linuxの/procを使うため、Linuxでのみ実行します。");
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "YourSinger.Tests",
            $"ffmpeg-lifetime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "fake-ffmpeg");
        var pidPath = Path.Combine(root, "child.pid");
        var outputPath = Path.Combine(root, "output.wav");
        int? pid = null;
        Task? extraction = null;
        using var cancellation = new CancellationTokenSource();

        try
        {
            // 改行コードがCRLFのチェックアウトでも、shebangが壊れないようにLFへ揃える。
            // PIDは一時ファイルへ書いてから置き換え、テストが書きかけのファイルを読まないようにする。
            var script = """
                #!/usr/bin/env python3
                import os
                import sys
                import time

                source = sys.argv[sys.argv.index("-i") + 1]
                temporary = source + ".tmp"
                with open(temporary, "w", encoding="utf-8") as stream:
                    stream.write(str(os.getpid()))
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(temporary, source)
                time.sleep(30)
                """.Replace("\r\n", "\n");
            await File.WriteAllTextAsync(executable, script, TestProject.Token);
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            extraction = new FfmpegAudioExtractor(executable)
                .ExtractAsync(pidPath, outputPath, cancellation.Token);

            for (var attempt = 0; attempt < 100 && !File.Exists(pidPath) && !extraction.IsCompleted; attempt++)
                await Task.Delay(50, TestProject.Token);

            // 起動に失敗して先に終了していたら、待ち続けず真因の例外を表に出す。
            if (extraction.IsCompleted)
                await extraction;

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
            // PIDを読む前に失敗しても、起動済みの子が残らないようにキャンセルして待つ。
            cancellation.Cancel();
            if (extraction is not null)
            {
                try
                {
                    await extraction;
                }
                catch (Exception)
                {
                    // 元の失敗を優先し、後始末中の例外では上書きしない。
                }
            }

            if (pid is int childPid)
            {
                try
                {
                    using var child = System.Diagnostics.Process.GetProcessById(childPid);
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
