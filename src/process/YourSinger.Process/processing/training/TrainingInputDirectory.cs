namespace YourSinger.Process.Processing.Training;

/// <summary>学習入力は一時ディレクトリで完成させ、成功したものだけ公開する。</summary>
internal static class TrainingInputDirectory
{
    public static string RequireComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.Any(x => char.IsControl(x) || "<>:\"/\\|?*".Contains(x)) ||
            value.EndsWith('.') || value.EndsWith(' '))
            throw new InvalidDataException("学習入力の識別子に使用できない文字があります。");
        return value;
    }

    public static async Task<T> BuildAsync<T>(string destination, Func<string, Task<T>> build,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(destination))
            throw new IOException("このジョブの学習入力は作成済みです。再学習用の新しいジョブを作成してください。");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".input-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        try
        {
            var result = await build(staging);
            cancellationToken.ThrowIfCancellationRequested();
            // 同時実行で出力先が先に作られた場合も既存成果物を上書きしない。
            Directory.Move(staging, destination);
            return result;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
