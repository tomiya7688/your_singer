using System.Security.Cryptography;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Dataset;

// {
//   責務: [MfaMeasurementFiles: 測定入力を作業領域内の通常ファイルとして安全に読む]
//   処理: [1: 相対パスとリンクを検証する 2: 大きさを制限して内容を読む]
// }
internal static class MfaMeasurementFiles
{
    internal const long MaxAudioBytes = 1024L * 1024 * 1024;
    private const int MaxMeasurementBytes = 16 * 1024 * 1024;
    internal const int MaxSingingDatasetBytes = 64 * 1024 * 1024;

    // { 責務: [Resolve: リンクを含まないproject内の既存通常ファイルを解決する] }
    internal static string Resolve(ProjectWorkspace workspace, string relativePath, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("測定入力にはproject相対パスが必要です。");
        var fullPath = Path.GetFullPath(relativePath, workspace.RootPath);
        var relative = Path.GetRelativePath(workspace.RootPath, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("測定入力がprojectの外です。");

        // { 処理: [各親のリンクやジャンクションを禁止する] }
        var current = Path.GetPathRoot(fullPath)!;
        foreach (var component in fullPath[current.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (component.Length == 0) continue;
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("測定入力のパスにリンクを使用できません。");
        }
        var info = new FileInfo(fullPath);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0 || info.Length <= 0 || info.Length > maxBytes)
            throw new InvalidDataException("測定入力はサイズ制限内の通常ファイルが必要です。");
        return fullPath;
    }

    // { 責務: [OpenRead: 開いたハンドルが検査したファイルを指すことを確認する] }
    internal static FileStream OpenRead(ProjectWorkspace workspace, string path, long maxBytes)
    {
        var resolved = Resolve(workspace, path, maxBytes);
        var stream = VerifiedFileInput.OpenRead(resolved);
        try
        {
            VerifiedFileInput.RequireExpected(stream, resolved);
            VerifiedFileInput.RequireRegular(stream);
            if (stream.Length <= 0 || stream.Length > maxBytes)
                throw new InvalidDataException("測定入力のサイズが不正です。");
            return stream;
        }
        catch (NotSupportedException exception)
        {
            stream.Dispose();
            throw new InvalidDataException("測定入力には通常ファイルが必要です。", exception);
        }
        catch { stream.Dispose(); throw; }
    }

    // { 責務: [ReadRecordAsync: 上限と読込中のサイズを検査して測定JSONを読む] }
    internal static Task<byte[]> ReadRecordAsync(ProjectWorkspace workspace, string path, CancellationToken token) =>
        ReadLimitedAsync(workspace, path, MaxMeasurementBytes, token);

    // { 責務: [ReadSingingDatasetAsync: 歌唱datasetを測定記録とは別の上限で読み込む] }
    internal static Task<byte[]> ReadSingingDatasetAsync(ProjectWorkspace workspace, string path, CancellationToken token) =>
        ReadLimitedAsync(workspace, path, MaxSingingDatasetBytes, token);

    // { 責務: [ReadLimitedAsync: 指定上限を守って入力バイト列を読み込む] }
    private static async Task<byte[]> ReadLimitedAsync(ProjectWorkspace workspace, string path, int maxBytes, CancellationToken token)
    {
        await using var stream = OpenRead(workspace, path, maxBytes);
        var length = stream.Length;
        if (length > maxBytes) throw new InvalidDataException("入力ファイルが大きすぎます。");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, token);
        if (await stream.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("入力ファイルが読込中に変更されました。");
        return bytes;
    }

    // { 責務: [Hash: 記録のSHA-256を小文字hexで返す] }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
