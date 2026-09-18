using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Media;

public static class SupportedMedia
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".flac", ".mp3", ".m4a", ".ogg", ".opus"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".m4v"
    };

    public static bool TryGetMediaType(string path, out MediaType mediaType)
    {
        var extension = Path.GetExtension(path);

        if (AudioExtensions.Contains(extension))
        {
            mediaType = MediaType.Audio;
            return true;
        }

        if (VideoExtensions.Contains(extension))
        {
            mediaType = MediaType.Video;
            return true;
        }

        mediaType = default;
        return false;
    }
}
