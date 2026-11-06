using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Tests;

// 同一話者のトーク・歌唱の共通特徴を、来歴付きで集約することを確認する(学習器への反映は対象外)。
public sealed class SharedSpeakerFeatureTests
{
    private static readonly SharedSpeakerFeatureService Service = new();
    private static readonly UsageRoutingService Router = new();

    private static UniversalVoiceSegment Seg(
        string id,
        string? speaker = "spk_a",
        double[]? embedding = null,
        double meanF0 = 200,
        double rate = 5)
    {
        var s = new UniversalVoiceSegment
        {
            SegmentId = id, SourceId = "src", AudioPath = $"{id}.wav", CacheKey = id, SpeakerId = speaker,
            MeanF0Hz = meanF0, F0StdDevHz = 10, PitchReliability = 0.9,
            StyleProsody = new StyleProsodyRecord { SpeakingRate = rate, PitchRangeSemitones = 4, EnergyVariation = 0.1, PauseRatio = 0.2 }
        };
        s.SpeakerEmbedding.AddRange(embedding ?? [1.0, 0.0]);
        return s;
    }

    private static SharedSpeakerFeatureSet Build(
        UniversalVoiceSegment[] segments,
        Dictionary<string, SegmentContentType> types,
        string[]? excluded = null,
        string speaker = "spk_a")
    {
        var classification = new ContentClassificationRecord
        {
            StageVersion = "test",
            Classifications = [.. types.Select(x => new SegmentContentClassification { SegmentId = x.Key, ContentType = x.Value, Confidence = 0.8 })]
        };
        var routing = Router.Route(segments, classification, new HashSet<string>(excluded ?? []));
        return Service.Build(segments, routing, speaker);
    }

    [Fact]
    public void SpeechOnly_SingingIsUnavailable()
    {
        var set = Build([Seg("a"), Seg("b", embedding: [0.0, 1.0])],
            new() { ["a"] = SegmentContentType.Speech, ["b"] = SegmentContentType.Speech });
        var speech = set.Find(SharedFeatureName.SpeakerEmbedding, SharedFeatureDomain.Speech);
        Assert.Equal(["a", "b"], speech.SegmentIds);
        Assert.Equal([0.5, 0.5], speech.Vector);
        var singing = set.Find(SharedFeatureName.SpeakerEmbedding, SharedFeatureDomain.Singing);
        Assert.Equal(ConfidenceStatus.Unavailable, singing.Status);
        Assert.Empty(singing.SegmentIds);
        Assert.Null(singing.Vector);
    }

    [Fact]
    public void SingingOnly_SpeechIsUnavailable()
    {
        var set = Build([Seg("s", meanF0: 300)], new() { ["s"] = SegmentContentType.Singing });
        Assert.Equal(ConfidenceStatus.Measured, set.Find(SharedFeatureName.F0Distribution, SharedFeatureDomain.Singing).Status);
        Assert.Equal(300, set.Find(SharedFeatureName.F0Distribution, SharedFeatureDomain.Singing).Scalars!["mean_f0_hz"]);
        Assert.Equal(ConfidenceStatus.Unavailable, set.Find(SharedFeatureName.F0Distribution, SharedFeatureDomain.Speech).Status);
    }

    [Fact]
    public void Mixed_KeepsBothDomainsWithSeparateProvenance()
    {
        var set = Build([Seg("talk", meanF0: 150), Seg("song", meanF0: 330)],
            new() { ["talk"] = SegmentContentType.Speech, ["song"] = SegmentContentType.Singing });
        var speech = set.Find(SharedFeatureName.F0Distribution, SharedFeatureDomain.Speech);
        var singing = set.Find(SharedFeatureName.F0Distribution, SharedFeatureDomain.Singing);
        Assert.Equal(["talk"], speech.SegmentIds);
        Assert.Equal(["song"], singing.SegmentIds);
        Assert.Equal(150, speech.Scalars!["mean_f0_hz"]);
        Assert.Equal(330, singing.Scalars!["mean_f0_hz"]);
        Assert.Equal(SharedSpeakerFeatureService.MeasurementVersion, speech.MeasurementVersion);
    }

    [Fact]
    public void F0StdDev_UsesTotalVariance()
    {
        var set = Build([Seg("a", meanF0: 100), Seg("b", meanF0: 200)],
            new() { ["a"] = SegmentContentType.Speech, ["b"] = SegmentContentType.Speech });
        var scalars = set.Find(SharedFeatureName.F0Distribution, SharedFeatureDomain.Speech).Scalars!;
        Assert.Equal(150, scalars["mean_f0_hz"]);
        Assert.Equal(Math.Sqrt(100 + 2500), scalars["f0_std_dev_hz"], 9);
    }

    [Fact]
    public void WrongSpeaker_NotMerged()
    {
        var set = Build([Seg("a", embedding: [1, 0]), Seg("other", "spk_b", embedding: [0, 1]), Seg("unknown", null, embedding: [0, 1])],
            new() { ["a"] = SegmentContentType.Speech, ["other"] = SegmentContentType.Speech, ["unknown"] = SegmentContentType.Speech });
        var speech = set.Find(SharedFeatureName.SpeakerEmbedding, SharedFeatureDomain.Speech);
        Assert.Equal(["a"], speech.SegmentIds);
        Assert.Equal([1.0, 0.0], speech.Vector);
        Assert.DoesNotContain(set.Skipped, x => x.SegmentId == "other");
    }

    [Fact]
    public void UserExcluded_NotMerged_AndRecordedAsSkipped()
    {
        var set = Build([Seg("a"), Seg("b", embedding: [0, 1])],
            new() { ["a"] = SegmentContentType.Speech, ["b"] = SegmentContentType.Speech }, excluded: ["b"]);
        Assert.Equal(["a"], set.Find(SharedFeatureName.SpeakerEmbedding, SharedFeatureDomain.Speech).SegmentIds);
        Assert.Contains(set.Skipped, x => x.SegmentId == "b" && x.Reason == UsageRoutingReason.UserExcluded);
    }

    [Fact]
    public void Ambiguous_NotMerged()
    {
        var set = Build([Seg("a")], new() { ["a"] = SegmentContentType.Ambiguous });
        Assert.All(set.Features, x => Assert.Equal(ConfidenceStatus.Unavailable, x.Status));
        Assert.Contains(set.Skipped, x => x.SegmentId == "a" && x.Reason == UsageRoutingReason.AmbiguousContent);
    }

    [Fact]
    public void NoEmbedding_ReportsUnavailable_WithoutFillingValues()
    {
        var empty = Seg("a");
        empty.SpeakerEmbedding.Clear();
        var set = Build([empty], new() { ["a"] = SegmentContentType.Speech });
        var embedding = set.Find(SharedFeatureName.SpeakerEmbedding, SharedFeatureDomain.Speech);
        Assert.Equal(ConfidenceStatus.Unavailable, embedding.Status);
        Assert.Null(embedding.Vector);
        Assert.Contains(set.Skipped, x => x.Feature == SharedFeatureName.SpeakerEmbedding && x.Reason == "embedding_unavailable");
        Assert.Equal(ConfidenceStatus.Measured, set.Find(SharedFeatureName.F0Distribution, SharedFeatureDomain.Speech).Status);
    }

    [Fact]
    public void InvalidValues_AndDimensionMismatch_AreSkippedWithReason()
    {
        var nan = Seg("nan", embedding: [double.NaN, 0]);
        var wide = Seg("wide", embedding: [1, 0, 0]);
        var noF0 = Seg("nof0", meanF0: 0);
        var set = Build([Seg("a"), nan, wide, noF0],
            new() { ["a"] = SegmentContentType.Speech, ["nan"] = SegmentContentType.Speech, ["wide"] = SegmentContentType.Speech, ["nof0"] = SegmentContentType.Speech });
        Assert.Equal(["a", "nof0"], set.Find(SharedFeatureName.SpeakerEmbedding, SharedFeatureDomain.Speech).SegmentIds);
        Assert.Contains(set.Skipped, x => x.SegmentId == "wide" && x.Reason == "embedding_dimension_mismatch");
        Assert.Contains(set.Skipped, x => x.SegmentId == "nan" && x.Reason == "embedding_unavailable");
        Assert.DoesNotContain("nof0", set.Find(SharedFeatureName.F0Distribution, SharedFeatureDomain.Speech).SegmentIds);
    }

    [Fact]
    public void Result_IsDeterministicAndOrderIndependent()
    {
        var segments = new[] { Seg("c"), Seg("a", embedding: [0, 1]), Seg("b") };
        var types = segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Speech);
        var first = Build(segments, types);
        var second = Build([.. segments.Reverse()], types);
        Assert.Equal(6, first.Features.Count);
        Assert.Equal(first.Features.Select(x => (x.Feature, x.Domain)), second.Features.Select(x => (x.Feature, x.Domain)));
        Assert.Equal(first.Find(SharedFeatureName.SpeakerEmbedding, SharedFeatureDomain.Speech).Vector,
            second.Find(SharedFeatureName.SpeakerEmbedding, SharedFeatureDomain.Speech).Vector);
    }

    [Fact]
    public void BlankSpeaker_Throws() =>
        Assert.Throws<ArgumentException>(() => Service.Build([], Router.Route([], new ContentClassificationRecord { StageVersion = "t" }), " "));
}
