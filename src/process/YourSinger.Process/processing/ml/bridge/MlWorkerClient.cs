using System.Diagnostics;
using System.Text.Json;
using YourSinger.Process.Processing.Ml.Protocol;

namespace YourSinger.Process.Processing.Ml.Bridge;

public sealed class MlWorkerClient
{
    private readonly string _workerPath;

    public MlWorkerClient(string? workerPath = null)
    {
        _workerPath = workerPath ?? ResolveWorkerPath();
    }

    public async Task<JsonElement> SendAsync(
        string command,
        object payload,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _workerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();

        var requestId = Guid.NewGuid().ToString("N");
        var request = new
        {
            request_id = requestId,
            command,
            payload
        };

        await process.StandardInput.WriteLineAsync(
            JsonSerializer.Serialize(request).AsMemory(),
            cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        var responseLine = await process.StandardOutput.ReadLineAsync(cancellationToken);
        var errorText = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (responseLine is null)
        {
            throw new InvalidOperationException(
                $"ML workerから応答がありません。{errorText}");
        }

        using var document = JsonDocument.Parse(responseLine);
        var root = document.RootElement;

        if (!root.TryGetProperty("status", out var status) ||
            status.GetString() != "ok")
        {
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : "不明なエラー";

            throw new InvalidOperationException(
                $"ML worker処理に失敗しました: {error}");
        }

        return root.GetProperty("result").Clone();
    }

    private static string ResolveWorkerPath()
    {
        var executableName = OperatingSystem.IsWindows()
            ? "YourSinger.ML.exe"
            : "YourSinger.ML";

        var bundledPath = Path.Combine(
            AppContext.BaseDirectory,
            "workers",
            executableName);

        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        throw new FileNotFoundException(
            "ML workerが見つかりません。開発時はworkerをビルドし、配布時はworkers/へ同梱してください。",
            bundledPath);
    }
}
