using System.Diagnostics;
using YourSinger.Process.Processing.Media;

namespace YourSinger.Process.Tests;

public sealed class FfmpegAudioExtractorTests
{
    [Fact]
    public async Task CancellationReapsFfmpegProcess()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Path.Combine(Path.GetTempPath(), "your-singer-ffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "fake-ffmpeg");
        var pidFile = Path.Combine(root, "pid.txt");
        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\necho $$ > \"$YOURSINGER_FFMPEG_TEST_PID_FILE\"\nsleep 60\n",
            TestProject.Token);
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var previousPidFile = Environment.GetEnvironmentVariable("YOURSINGER_FFMPEG_TEST_PID_FILE");
        Environment.SetEnvironmentVariable("YOURSINGER_FFMPEG_TEST_PID_FILE", pidFile);
        int pid = 0;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestProject.Token);
            var extractor = new FfmpegAudioExtractor(script);
            var task = extractor.ExtractAsync(
                Path.Combine(root, "input.wav"),
                Path.Combine(root, "output.wav"),
                cancellation.Token);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!File.Exists(pidFile) && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestProject.Token);
            Assert.True(File.Exists(pidFile), "fake FFmpeg did not start");
            pid = int.Parse((await File.ReadAllTextAsync(pidFile, TestProject.Token)).Trim());

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => task.WaitAsync(TimeSpan.FromSeconds(10), TestProject.Token));

            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Directory.Exists($"/proc/{pid}") && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestProject.Token);
            Assert.False(Directory.Exists($"/proc/{pid}"), "cancelled FFmpeg process was not reaped");
        }
        finally
        {
            Environment.SetEnvironmentVariable("YOURSINGER_FFMPEG_TEST_PID_FILE", previousPidFile);
            if (pid > 0)
            {
                try
                {
                    using var leaked = System.Diagnostics.Process.GetProcessById(pid);
                    leaked.Kill(entireProcessTree: true);
                    await leaked.WaitForExitAsync(TestProject.Token);
                }
                catch (ArgumentException) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
