using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

public sealed class UsageDatasetRouter
{
    public UsageDatasetSelection Build(ContentClassificationRecord classification)
    {
        var result = new UsageDatasetSelection();

        foreach (var item in classification.Classifications)
        {
            switch (item.ContentType)
            {
                case SegmentContentType.Speech:
                    result.TalkSegmentIds.Add(item.SegmentId);
                    break;
                case SegmentContentType.Singing:
                    result.SingingSegmentIds.Add(item.SegmentId);
                    break;
                case SegmentContentType.Ambiguous:
                    result.AmbiguousSegmentIds.Add(item.SegmentId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        return result;
    }
}
