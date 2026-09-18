namespace YourSinger.Process.Processing.Audio;

public sealed record AudioPreprocessingProgress(
    string SourceId,
    string Stage,
    double Progress,
    string Message);
