using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace YourSinger.Process.Processing.Ml.Bridge;

public sealed class MlWorkerClient
{
    private readonly string _workerPath;
    private readonly IReadOnlyList<string> _arguments;

    public MlWorkerClient(string? workerPath = null, IReadOnlyList<string>? arguments = null)
    {
        _workerPath = workerPath ?? ResolveWorkerPath();
        _arguments = arguments ?? [];
    }

    public async Task<JsonElement> SendAsync(string command, object payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo
        {
            FileName = _workerPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var arg in _arguments) info.ArgumentList.Add(arg);
        using var process = new System.Diagnostics.Process { StartInfo = info };
        process.Start();
        var output = ReadAsync(process.StandardOutput, process, false, cancellationToken);
        var errors = ReadAsync(process.StandardError, process, true, cancellationToken);
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                request_id = requestId, command, payload
            }).AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, errors);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"音声処理ワーカーが異常終了しました。終了コード: {process.ExitCode}\n{errors.Result}");
            using var document = JsonDocument.Parse(output.Result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("request_id", out var id) || id.GetString() != requestId)
                throw new InvalidDataException("音声処理ワーカーの要求IDが一致しません。");
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "ok")
                throw new InvalidOperationException("音声処理に失敗しました: " +
                    (root.TryGetProperty("error", out var error) ? error.ToString() : "理由不明"));
            if (!root.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null)
                throw new InvalidDataException("音声処理ワーカーの結果がありません。");
            return result.Clone();
        }
        finally
        {
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, errors); }
            catch (Exception) { /* 本来の処理エラーを保つ。読み取り例外は必ず回収する。 */ }
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, System.Diagnostics.Process process,
        bool keepTail, CancellationToken token)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        var limit = keepTail ? 65536 : 4 * 1024 * 1024;
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token);
                if (count == 0) break;
                text.Append(buffer, 0, count);
                if (text.Length <= limit) continue;
                if (!keepTail) throw new InvalidDataException("音声処理ワーカーの応答が上限を超えました。");
                text.Remove(0, text.Length - limit);
            }
            return text.ToString();
        }
        catch { Stop(process); throw; }
    }

    private static void Stop(System.Diagnostics.Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static string ResolveWorkerPath()
    {
        var executable = OperatingSystem.IsWindows() ? "YourSinger.ML.exe" : "YourSinger.ML";
        var path = Path.Combine(AppContext.BaseDirectory, "workers", executable);
        return File.Exists(path) ? path : throw new FileNotFoundException("音声処理ワーカーが同梱されていません。配布物を確認してください。", path);
    }
}
