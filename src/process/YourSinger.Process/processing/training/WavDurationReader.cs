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
                    reader.ReadUInt32();
                    byteRate = reader.ReadUInt32();
                    stream.Position += size - 8;
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
