using YourSinger.Data.Models;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 発話由来の声質参照(経路S1)の選び方と、消費確認までの記録を確認する。
// DiffSinger 固定版は話者エンコーダ条件を受け取れないため、既定は未対応。実音声での効果は未検証。
public sealed class SingingTimbreAdaptationTests
{
    private static readonly SingingTimbreAdaptationService Service = new();

    private static UniversalVoiceSegment Seg(string id, string? speaker = "spk_a", double voiced = 0.8, string cache = "") => new()
    {
        SegmentId = id, SourceId = "src", AudioPath = $"segments/{id}.wav", CacheKey = cache.Length > 0 ? cache : id, SpeakerId = speaker,
        VoicedRatio = voiced, VoicingFeaturePath = $"{id}-voicing.json"
    };

    private static SingingTimbreAdaptationInput Build(
        Dictionary<string, SegmentContentType> types, UniversalVoiceSegment[] segments, bool enabled = true,
        bool supports = false, string[]? excluded = null, string speaker = "spk_a")
    {
        foreach (var segment in segments)
            if (types.TryGetValue(segment.SegmentId, out var type)) segment.ContentType = type;
        var classification = new ContentClassificationRecord
        {
            StageVersion = "t",
            Classifications = [.. types.Select(x => new SegmentContentClassification { SegmentId = x.Key, ContentType = x.Value, Confidence = 0.8 })]
        };
        return Service.Build(segments, classification, speaker, enabled, supports, new HashSet<string>(excluded ?? []));
    }

    private static Dictionary<string, SegmentContentType> Speech(params UniversalVoiceSegment[] segments) =>
        segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Speech);

    [Fact]
    public void SpeechOnly_ZeroSinging_UsesSpeechSegmentsAsReferences_AndDefaultsToUnsupported()
    {
        var segments = new[] { Seg("b"), Seg("a") };
        var input = Build(Speech(segments), segments);
        Assert.Equal(["a", "b"], input.References.Select(x => x.SegmentId));
        Assert.All(input.References, x => Assert.Equal(1.0, x.Weight));
        Assert.False(input.Usage.Used);
        Assert.Equal(SharedFeatureSupport.Unsupported, input.Usage.Support);
        Assert.Contains("未対応", input.Usage.Reason);
    }

    [Fact]
    public void WhenTheBackendSupportsIt_UsageStaysUnusedUntilTheAdapterConfirmsConsumption()
    {
        var segments = new[] { Seg("a"), Seg("b") };
        var input = Build(Speech(segments), segments, supports: true);
        Assert.Equal(SharedFeatureSupport.Supported, input.Usage.Support);
        Assert.False(input.Usage.Used);
        var confirmed = SingingTimbreAdaptationService.Confirm(input, new SingingTimbreConsumption(
            ["b", "a"], SingingTimbreAdaptationInput.Purpose, SingingTimbreAdaptationInput.Method, input.InputHash));
        Assert.True(confirmed.Used);
        Assert.Contains("未検証", confirmed.Reason);
    }

    [Fact]
    public void Confirm_RejectsMismatchedHashSegmentsPurposeAndUnsupportedInputs()
    {
        var segments = new[] { Seg("a"), Seg("b") };
        var supported = Build(Speech(segments), segments, supports: true);
        string[] ids = ["a", "b"];
        Assert.Throws<InvalidOperationException>(() => SingingTimbreAdaptationService.Confirm(supported,
            new("a".Split(), SingingTimbreAdaptationInput.Purpose, SingingTimbreAdaptationInput.Method, supported.InputHash)));
        Assert.Throws<InvalidOperationException>(() => SingingTimbreAdaptationService.Confirm(supported,
            new(ids, SingingTimbreAdaptationInput.Purpose, SingingTimbreAdaptationInput.Method, "other")));
        Assert.Throws<InvalidOperationException>(() => SingingTimbreAdaptationService.Confirm(supported,
            new(ids, "teacher", SingingTimbreAdaptationInput.Method, supported.InputHash)));
        var unsupported = Build(Speech(segments), segments, supports: false);
        Assert.Throws<InvalidOperationException>(() => SingingTimbreAdaptationService.Confirm(unsupported,
            new(ids, SingingTimbreAdaptationInput.Purpose, SingingTimbreAdaptationInput.Method, unsupported.InputHash)));
    }

    [Fact]
    public void SingingSegments_AreTheTeacher_NotTimbreReferences()
    {
        var segments = new[] { Seg("talk"), Seg("song") };
        var input = Build(new() { ["talk"] = SegmentContentType.Speech, ["song"] = SegmentContentType.Singing }, segments);
        Assert.Equal(["talk"], input.References.Select(x => x.SegmentId));
    }

    [Fact]
    public void OtherSpeakerUnknownSpeakerExcludedAndSilent_AreNeverUsed()
    {
        var segments = new[] { Seg("mine"), Seg("other", "spk_b"), Seg("unknown", null), Seg("excluded"), Seg("silent", voiced: 0) };
        var input = Build(Speech(segments), segments, excluded: ["excluded"]);
        Assert.Equal(["mine"], input.References.Select(x => x.SegmentId));
        Assert.Contains(input.Skipped, x => x.SegmentId == "excluded" && x.Reason == "user_excluded");
        Assert.Contains(input.Skipped, x => x.SegmentId == "silent" && x.Reason == "no_usable_voice");
    }

    [Fact]
    public void ToggleOff_AndNoSpeech_RecordNotUsedWithReason()
    {
        var segments = new[] { Seg("a") };
        var off = Build(Speech(segments), segments, enabled: false);
        Assert.False(off.HasReferences);
        Assert.Contains("無効", off.Usage.Reason);
        var singing = new[] { Seg("s") };
        var none = Build(new() { ["s"] = SegmentContentType.Singing }, singing);
        Assert.False(none.HasReferences);
        Assert.Contains("発話区間がありません", none.Usage.Reason);
    }

    [Fact]
    public void InputHash_ChangesWithTheReferenceSet_AndIsOrderIndependent()
    {
        var segments = new[] { Seg("c"), Seg("a"), Seg("b") };
        var first = Build(Speech(segments), segments);
        var reversed = Build(Speech(segments), [.. segments.Reverse()]);
        Assert.Equal(first.InputHash, reversed.InputHash);
        var fewer = Build(Speech(segments), segments, excluded: ["c"]);
        Assert.NotEqual(first.InputHash, fewer.InputHash);
        var changedAudio = new[] { Seg("c", cache: "changed"), Seg("a"), Seg("b") };
        Assert.NotEqual(first.InputHash, Build(Speech(changedAudio), changedAudio).InputHash);
        Assert.Throws<ArgumentException>(() => Build(Speech(segments), segments, speaker: " "));
    }
}
