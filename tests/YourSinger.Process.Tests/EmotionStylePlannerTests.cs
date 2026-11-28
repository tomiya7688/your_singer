using YourSinger.Data.Models;
using YourSinger.Process.Processing.Talk;

namespace YourSinger.Process.Tests;

// 感情別スタイルの作成計画(件数・話者・ラベルの扱い)を確認する。実音声でのスタイル品質は未評価。
public sealed class EmotionStylePlannerTests
{
    private static UniversalVoiceSegment Seg(string id, string? emotion, string source = EmotionLabelRecord.SourceUser,
        string? speaker = "spk_a", SegmentContentType type = SegmentContentType.Speech) => new()
        {
            SegmentId = id,
            SourceId = "src",
            AudioPath = $"segments/{id}.wav",
            CacheKey = id,
            SpeakerId = speaker,
            ContentType = type,
            VoicedRatio = 0.8,
            VoicingFeaturePath = $"{id}-voicing.json",
            Emotion = emotion is null ? null : new EmotionLabelRecord
            {
                Name = emotion,
                Source = source,
                Confidence = source == EmotionLabelRecord.SourceInferred ? 0.7 : null
            }
        };

    private static EmotionStylePlan Plan(UniversalVoiceSegment[] segments, int min, bool inferred = false, string[]? excluded = null)
    {
        var classification = new ContentClassificationRecord
        {
            StageVersion = "t",
            Classifications = [.. segments.Select(x => new SegmentContentClassification { SegmentId = x.SegmentId, ContentType = x.ContentType, Confidence = 0.8 })]
        };
        return new EmotionStylePlanner().Plan(segments, classification, "spk_a", min, inferred, new HashSet<string>(excluded ?? []));
    }

    [Fact]
    public void GroupsByEmotion_AndSkipsEmotionsBelowMinimumWithReason()
    {
        var plan = Plan([Seg("a", "嬉しい"), Seg("b", "嬉しい"), Seg("c", "怒り")], 2);
        Assert.Equal(["嬉しい"], plan.Styles.Select(x => x.Emotion));
        Assert.Equal(["a", "b"], plan.Styles[0].SegmentIds);
        var skip = Assert.Single(plan.Skipped);
        Assert.Equal("怒り", skip.Emotion);
        Assert.Equal(1, skip.SegmentCount);
        Assert.Contains("足りません", skip.Reason);
    }

    [Fact]
    public void Unlabeled_IsCounted_NotTreatedAsNormal()
    {
        var plan = Plan([Seg("a", null), Seg("b", "嬉しい")], 1);
        Assert.Equal(1, plan.UnlabeledSegmentCount);
        Assert.DoesNotContain(plan.Styles, x => x.Emotion == "平常");
    }

    [Fact]
    public void InferredLabels_AreIgnoredUnlessAllowed()
    {
        var segments = new[] { Seg("a", "嬉しい", EmotionLabelRecord.SourceInferred) };
        Assert.Empty(Plan(segments, 1).Styles);
        Assert.Single(Plan(segments, 1, inferred: true).Styles);
    }

    [Fact]
    public void SingingOtherSpeakerAndExcluded_AreNotUsed()
    {
        var plan = Plan(
            [Seg("song", "嬉しい", type: SegmentContentType.Singing), Seg("other", "嬉しい", speaker: "spk_b"), Seg("ex", "嬉しい"), Seg("ok", "嬉しい")],
            1, excluded: ["ex"]);
        Assert.Equal(["ok"], Assert.Single(plan.Styles).SegmentIds);
    }

    [Fact]
    public void RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan([Seg("a", "嬉しい")], 0));
        Assert.Throws<ArgumentException>(() => new EmotionStylePlanner().Plan([], new ContentClassificationRecord { StageVersion = "t" }, " ", 1));
    }
}
