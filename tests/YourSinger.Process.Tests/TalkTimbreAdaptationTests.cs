using YourSinger.Data.Models;
using YourSinger.Process.Processing.Talk;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 歌唱由来の声質参照(経路T1)の選び方を確認する。実音声での読み上げ評価は別(未実施)。
public sealed class TalkTimbreAdaptationTests
{
    private static readonly TalkTimbreAdaptationService Service = new();

    private static UniversalVoiceSegment Seg(string id, string? speaker = "spk_a", double voiced = 0.8) => new()
    {
        SegmentId = id, SourceId = "src", AudioPath = $"segments/{id}.wav", CacheKey = id, SpeakerId = speaker,
        VoicedRatio = voiced, VoicingFeaturePath = $"{id}-voicing.json"
    };

    private static TalkTimbreAdaptationInput Build(
        Dictionary<string, SegmentContentType> types,
        UniversalVoiceSegment[] segments,
        bool enabled = true,
        string[]? excluded = null,
        string speaker = "spk_a")
    {
        foreach (var segment in segments)
            if (types.TryGetValue(segment.SegmentId, out var type)) segment.ContentType = type;
        var classification = new ContentClassificationRecord
        {
            StageVersion = "t",
            Classifications = [.. types.Select(x => new SegmentContentClassification { SegmentId = x.Key, ContentType = x.Value, Confidence = 0.8 })]
        };
        return Service.Build(segments, classification, speaker, enabled, new HashSet<string>(excluded ?? []));
    }

    [Fact]
    public void SingingOnly_ZeroSpeech_UsesSingingSegmentsAsReferences()
    {
        var segments = new[] { Seg("b"), Seg("a") };
        var input = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Singing), segments);
        Assert.Equal(["a", "b"], input.References.Select(x => x.SegmentId));
        Assert.True(input.HasReferences);
        Assert.True(input.Usage.Used);
        Assert.Equal(SharedFeatureSupport.Supported, input.Usage.Support);
        Assert.Contains(TalkTimbreAdaptationInput.StyleName, input.Usage.Reason);
    }

    [Fact]
    public void SpeechSegments_AreTheTeacher_NotTimbreReferences()
    {
        var segments = new[] { Seg("talk"), Seg("song") };
        var input = Build(new() { ["talk"] = SegmentContentType.Speech, ["song"] = SegmentContentType.Singing }, segments);
        Assert.Equal(["song"], input.References.Select(x => x.SegmentId));
    }

    [Fact]
    public void OtherSpeakerAndUnknownSpeaker_AreNeverUsed()
    {
        var segments = new[] { Seg("mine"), Seg("other", "spk_b"), Seg("unknown", null) };
        var input = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Singing), segments);
        Assert.Equal(["mine"], input.References.Select(x => x.SegmentId));
    }

    [Fact]
    public void UserExcludedAndSilentSegments_AreSkippedWithReason()
    {
        var segments = new[] { Seg("ok"), Seg("excluded"), Seg("silent", voiced: 0) };
        var input = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Singing), segments, excluded: ["excluded"]);
        Assert.Equal(["ok"], input.References.Select(x => x.SegmentId));
        Assert.Contains(input.Skipped, x => x.SegmentId == "excluded" && x.Reason == "user_excluded");
        Assert.Contains(input.Skipped, x => x.SegmentId == "silent" && x.Reason == "no_usable_voice");
    }

    [Fact]
    public void AmbiguousSegments_AreNotUsed()
    {
        var segments = new[] { Seg("maybe") };
        var input = Build(new() { ["maybe"] = SegmentContentType.Ambiguous }, segments);
        Assert.False(input.HasReferences);
        Assert.False(input.Usage.Used);
        Assert.Contains("ambiguous_content", input.Skipped.Single().Reason);
    }

    [Fact]
    public void NoSingingOrDisabled_RecordsNotUsedWithReason()
    {
        var speechOnly = new[] { Seg("a") };
        var none = Build(new() { ["a"] = SegmentContentType.Speech }, speechOnly);
        Assert.False(none.Usage.Used);
        Assert.Contains("歌唱区間がありません", none.Usage.Reason);
        var singing = new[] { Seg("s") };
        var off = Build(new() { ["s"] = SegmentContentType.Singing }, singing, enabled: false);
        Assert.False(off.HasReferences);
        Assert.False(off.Usage.Used);
        Assert.Contains("無効", off.Usage.Reason);
    }

    [Fact]
    public void SameInput_GivesSameReferencesRegardlessOfOrder_AndBlankSpeakerThrows()
    {
        var segments = new[] { Seg("c"), Seg("a"), Seg("b") };
        var types = segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Singing);
        var first = Build(types, segments);
        var second = Build(types, [.. segments.Reverse()]);
        Assert.Equal(first.References, second.References);
        Assert.Throws<ArgumentException>(() => Build(types, segments, speaker: " "));
    }
}
