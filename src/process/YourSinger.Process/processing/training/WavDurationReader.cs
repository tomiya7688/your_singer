using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Training;

// 作業領域内のWAVヘッダから再生秒数を読む。読めない・領域外・不正なら null(0秒と読み替えない)。
public static class WavDurationReader
{
    public static double? Read(ProjectWorkspace workspace, UniversalVoiceSegment segment)
    {
        if (string.IsNullOrWhiteSpace(segment.AudioPath)) return null;
        try
        {
            var root = Path.GetFullPath(workspace.RootPath);
            var path = Path.GetFullPath(Path.Combine(root, segment.AudioPath));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                return null;
            return ReadFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    // {
    //   責務: [ReadFile: 指定したWAVファイルのヘッダから再生秒数を読む(領域内かどうかは呼び出し側が確認する)]
    //   引数: [path: WAVファイルの絶対パス]
    //   戻り値: [seconds: 再生秒数。読めない・不正なら null(0秒と読み替えない)]
    // }
    public static double? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 12 || new string(reader.ReadChars(4)) != "RIFF") return null;
            reader.ReadUInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") return null;
            uint byteRate = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadUInt32();
                if (id == "fmt " && size >= 16)
                {
                    // fmt の並び: 形式(2)・チャンネル数(2)・サンプルレート(4)・バイトレート(4)。バイトレートは先頭から8バイト目。
                    reader.ReadUInt32();
                    reader.ReadUInt32();
                    byteRate = reader.ReadUInt32();
                    stream.Position += size - 12 + (size & 1);
                }
                else if (id == "data")
                {
                    var bytes = Math.Min(size, (uint)Math.Max(0, stream.Length - stream.Position));
                    return byteRate > 0 ? (double)bytes / byteRate : null;
                }
                else
                {
                    stream.Position += size + (size & 1);
                }
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }
}
