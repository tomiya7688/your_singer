using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Tests;

// 会話・歌唱混在素材の用途別routing(役割と理由)を確認する。分類モデルは対象外。
public sealed class UsageDatasetRouterTests
{
    private static readonly UsageRoutingService Service = new();

    private static UniversalVoiceSegment Seg(string id, string? speaker = "spk_a") => new()
    {
        SegmentId = id, SourceId = "src", AudioPath = $"{id}.wav", CacheKey = id, SpeakerId = speaker
    };

    private static SegmentContentClassification Cls(
        string id,
        SegmentContentType type,
        double confidence = 0.8,
        bool overridden = false) => new()
    {
        SegmentId = id, ContentType = type, Confidence = confidence, UserOverridden = overridden
    };

    private static ContentClassificationRecord Record(
        IEnumerable<SegmentContentClassification> items,
        params ContentClassificationOverride[] overrides) => new()
    {
        StageVersion = "test", Classifications = [.. items], Overrides = [.. overrides]
    };

    [Fact]
    public void McPlusSinging_SplitsPrimary()
    {
        var result = Service.Route(
            [Seg("talk"), Seg("song")],
            Record([Cls("talk", SegmentContentType.Speech), Cls("song", SegmentContentType.Singing)]));
        var talk = result.Decisions.Single(x => x.SegmentId == "talk");
        var song = result.Decisions.Single(x => x.SegmentId == "song");
        Assert.Equal(UsageRole.Primary, talk.Talk.Role);
        Assert.Equal(UsageRole.FeatureOnly, talk.Singing.Role);
        Assert.Equal([UsageRoutingReason.SpeakerReference], talk.Singing.ReasonCodes);
        Assert.Equal(UsageRole.Primary, song.Singing.Role);
        Assert.Equal(UsageRole.FeatureOnly, song.Talk.Role);
        Assert.Equal(1, result.Count("talk", UsageRole.Primary));
        Assert.Equal(1, result.Count("talk", UsageRole.FeatureOnly));
    }

    [Fact]
    public void AllAmbiguous_IsReview_AndNeverPrimary()
    {
        var result = Service.Route(
            [Seg("a"), Seg("b")],
            Record([Cls("a", SegmentContentType.Ambiguous), Cls("b", SegmentContentType.Ambiguous)]));
        Assert.All(result.Decisions, x =>
        {
            Assert.Equal(UsageRole.Review, x.Talk.Role);
            Assert.Equal(UsageRole.Review, x.Singing.Role);
            Assert.Equal("needs_review", x.Talk.Status);
            Assert.Equal([UsageRoutingReason.AmbiguousContent], x.Singing.ReasonCodes);
        });
        Assert.Equal(0, result.Count("talk", UsageRole.Primary) + result.Count("singing", UsageRole.Primary));
    }

    [Fact]
    public void ManualOverride_Wins_AndKeepsOriginalLabel()
    {
        var result = Service.Route(
            [Seg("a")],
            Record(
                [Cls("a", SegmentContentType.Singing, 0.9, overridden: true)],
                new ContentClassificationOverride
                {
                    SegmentId = "a", OriginalType = SegmentContentType.Speech, NewType = SegmentContentType.Singing
                }));
        var decision = Assert.Single(result.Decisions);
        Assert.Equal("manual", decision.ClassificationOrigin);
        Assert.Equal(SegmentContentType.Speech, decision.OriginalContentType);
        Assert.Equal(UsageRole.Primary, decision.Singing.Role);
        Assert.Equal(UsageRole.FeatureOnly, decision.Talk.Role);
        Assert.Null(decision.ClassificationConfidence);
        Assert.Equal(ConfidenceStatus.Unavailable, decision.ClassificationConfidenceStatus);
    }

    [Fact]
    public void MixedSpeakers_Separated_AndUnknownSpeakerIsNotSharedFeature()
    {
        var result = Service.Route(
            [Seg("a", "spk_a"), Seg("b", "spk_b"), Seg("c", null)],
            Record(
            [
                Cls("a", SegmentContentType.Speech), Cls("b", SegmentContentType.Singing),
                Cls("c", SegmentContentType.Speech)
            ]));
        Assert.Equal(["a"], result.ForSpeaker("spk_a").Select(x => x.SegmentId));
        Assert.Equal(["b"], result.ForSpeaker("spk_b").Select(x => x.SegmentId));
        var unknown = result.Decisions.Single(x => x.SegmentId == "c");
        Assert.Equal(UsageRole.Excluded, unknown.Singing.Role);
        Assert.Equal([UsageRoutingReason.SpeakerUnconfirmed], unknown.Singing.ReasonCodes);
    }

    [Fact]
    public void UserExcluded_Reflected_ForBothTargets()
    {
        var result = Service.Route(
            [Seg("a")], Record([Cls("a", SegmentContentType.Speech)]), new HashSet<string> { "a" });
        var decision = Assert.Single(result.Decisions);
        Assert.Equal(UsageRole.Excluded, decision.Talk.Role);
        Assert.Equal(UsageRole.Excluded, decision.Singing.Role);
        Assert.Equal([UsageRoutingReason.UserExcluded], decision.Talk.ReasonCodes);
    }

    [Fact]
    public void ZeroConfidence_IsUnknownNotRejection_AndMissingClassificationIsReview()
    {
        var result = Service.Route(
            [Seg("a"), Seg("b"), Seg("c")],
            Record([Cls("a", SegmentContentType.Speech, 0), Cls("c", SegmentContentType.Speech, double.NaN)]));
        var zero = result.Decisions.Single(x => x.SegmentId == "a");
        Assert.Equal(UsageRole.Primary, zero.Talk.Role);
        Assert.Null(zero.ClassificationConfidence);
        Assert.Equal(ConfidenceStatus.LegacyUnknown, zero.ClassificationConfidenceStatus);
        var missing = result.Decisions.Single(x => x.SegmentId == "b");
        Assert.Equal(UsageRole.Review, missing.Talk.Role);
        Assert.Equal([UsageRoutingReason.ClassificationMissing], missing.Talk.ReasonCodes);
        Assert.Equal(ConfidenceStatus.Invalid,
            result.Decisions.Single(x => x.SegmentId == "c").ClassificationConfidenceStatus);
    }

    [Fact]
    public void ExistingSelectionApi_Unchanged()
    {
        var selection = new UsageDatasetRouter().Build(Record(
        [
            Cls("talk", SegmentContentType.Speech), Cls("song", SegmentContentType.Singing),
            Cls("maybe", SegmentContentType.Ambiguous)
        ]));
        Assert.Equal(["talk"], selection.TalkSegmentIds);
        Assert.Equal(["song"], selection.SingingSegmentIds);
        Assert.Equal(["maybe"], selection.AmbiguousSegmentIds);
    }

    [Fact]
    public void Result_IsOrderedAndIndependentOfInputOrder()
    {
        var segments = new[] { Seg("c"), Seg("a"), Seg("b") };
        var classification = Record(segments.Select(x => Cls(x.SegmentId, SegmentContentType.Speech)));
        var first = Service.Route(segments, classification);
        var second = Service.Route(segments.Reverse(), classification);
        Assert.Equal(["a", "b", "c"], first.Decisions.Select(x => x.SegmentId));
        Assert.Equal(first.Decisions.Select(x => x.SegmentId), second.Decisions.Select(x => x.SegmentId));
    }
}
