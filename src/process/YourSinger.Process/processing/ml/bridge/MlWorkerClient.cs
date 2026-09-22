using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace YourSinger.Process.Processing.Ml.Bridge;

public sealed class MlWorkerClient
{
    private readonly string _workerPath;
    private readonly string[] _arguments;

    // 既定は同梱実行ファイルのみ。引数付き起動は開発・プロセス境界テストで明示指定する。
    public MlWorkerClient(string? workerPath = null, IReadOnlyList<string>? arguments = null)
    {
        _workerPath = workerPath ?? ResolveWorkerPath();
        _arguments = arguments?.ToArray() ?? [];
    }

    public async Task<JsonElement> SendAsync(string command, object payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = _workerPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var argument in _arguments) startInfo.ArgumentList.Add(argument);
        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("MLワーカーを起動できませんでした。");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("MLワーカーを起動できません。同梱ファイルを確認してください。", exception);
        }

        // 応答を待つ間も両方のパイプを読み続け、大量ログによる相互待ちを防ぐ。
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = ReadErrorTailAsync(process.StandardError);
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var request = new { request_id = requestId, command, payload };
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var errors = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"MLワーカーが異常終了しました（終了コード {process.ExitCode}）。{errors}");
            if (string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException($"MLワーカーから応答がありません。{errors}");
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("request_id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != requestId)
                throw new InvalidDataException("MLワーカーの応答IDが要求と一致しません。");
            if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "ok")
            {
                var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString() : "不明なエラー";
                throw new InvalidOperationException($"MLワーカー処理に失敗しました: {error}");
            }
            if (!root.TryGetProperty("result", out var result))
                throw new InvalidDataException("MLワーカーの応答に処理結果がありません。");
            return result.Clone();
        }
        catch
        {
            // WaitForExitAsyncのキャンセルだけでは子プロセスは終了しない。
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }
    }

    private static async Task<string> ReadErrorTailAsync(StreamReader reader)
    {
        const int limit = 128 * 1024;
        var tail = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > limit) tail.Remove(0, tail.Length - limit);
        }
        return tail.ToString();
    }

    private static string ResolveWorkerPath()
    {
        var executable = OperatingSystem.IsWindows() ? "YourSinger.ML.exe" : "YourSinger.ML";
        var path = Path.Combine(AppContext.BaseDirectory, "workers", executable);
        if (File.Exists(path)) return path;
        throw new FileNotFoundException("MLワーカーが見つかりません。同梱ファイルを確認してください。", path);
    }
}
