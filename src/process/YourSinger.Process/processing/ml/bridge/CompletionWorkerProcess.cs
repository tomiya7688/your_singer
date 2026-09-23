using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace YourSinger.Process.Processing.Ml.Bridge;

public interface IPhonemeCandidateWorker
{
    Task<JsonElement> GenerateAsync(object payload, CancellationToken cancellationToken = default);
}

/// <summary>依存関係を分離した補完専用ワーカー。端末側のPythonは起動しない。</summary>
public sealed class CompletionWorkerProcess : IPhonemeCandidateWorker
{
    public async Task<JsonElement> GenerateAsync(object payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = Path.Combine(AppContext.BaseDirectory, "workers", "completion",
            OperatingSystem.IsWindows() ? "YourSinger.Completion.exe" : "YourSinger.Completion");
        if (!File.Exists(executable))
            throw new FileNotFoundException("補完専用ワーカーが同梱されていません。補完対応の配布構成が必要です。", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        using var process = new System.Diagnostics.Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("補完ワーカーを起動できません。");
        // 両パイプを同時に読み続け、巨大ログは末尾だけ保持する。
        using var pipes = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdout = DrainAsync(process.StandardOutput, 262144, false, pipes.Token);
        var stderr = DrainAsync(process.StandardError, 16384, true, pipes.Token);
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            var request = JsonSerializer.Serialize(new { request_id = requestId, command = "generate_phoneme_candidates", payload });
            await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr).WaitAsync(cancellationToken);
            var output = await stdout;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"補完ワーカーが異常終了しました（終了コード {process.ExitCode}）。");
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("request_id", out var id) ||
                id.ValueKind != JsonValueKind.String || id.GetString() != requestId)
                throw new InvalidDataException("補完ワーカーの応答IDが一致しません。");
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "ok")
            {
                var message = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString() : "詳細不明";
                throw new InvalidOperationException($"補完候補を生成できませんでした。{message}");
            }
            return root.GetProperty("result").Clone();
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await pipes.CancelAsync();
            // 例外・キャンセル時もパイプ読取タスクを残さない。
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception) { /* 先に発生した処理例外を維持する。 */ }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, int limit, bool tail, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        var overflow = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (tail)
            {
                result.Append(buffer, 0, count);
                if (result.Length > limit) result.Remove(0, result.Length - limit);
            }
            else
            {
                var remaining = limit - result.Length;
                result.Append(buffer, 0, Math.Min(remaining, count));
                overflow |= count > remaining;
            }
        }
        if (overflow) throw new InvalidDataException("補完ワーカーの応答が上限を超えました。");
        return result.ToString();
    }
}
