namespace YourSinger.Process.Processing.Media;

public enum MediaScanProgressKind
{
    Enumerating,
    Hashing,
    Unchanged,
    Changed,
    Added,
    Extracting,
    Completed,
    Skipped,
    Failed
}

public sealed record MediaScanProgress(
    MediaScanProgressKind Kind,
    string Path,
    int Processed,
    int Total,
    string Message);
