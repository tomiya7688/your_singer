using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

public sealed class UniversalDatasetViewBuilder
{
    public IReadOnlyList<UniversalVoiceSegment> BuildTalkView(
        UniversalVoiceDatasetRecord dataset) =>
        dataset.Segments
            .Where(x => x.ContentType == SegmentContentType.Speech)
            .ToArray();

    public IReadOnlyList<UniversalVoiceSegment> BuildSingingView(
        UniversalVoiceDatasetRecord dataset) =>
        dataset.Segments
            .Where(x => x.ContentType == SegmentContentType.Singing)
            .ToArray();
}
