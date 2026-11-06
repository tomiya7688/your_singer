using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Tests;

// 音素・音域の希少度からのUtility(0〜1)と、根拠が無い場合の中立値を確認する。
public sealed class TrainingUtilityTests
{
    private static readonly TrainingUtilityService Service = new();

    private static UtilityObservation Obs(
        string id,
        string[] phonemes,
        double[]? f0 = null,
        double reliability = 0.9,
        string method = "mfa-japanese-phone-forced-alignment-v1")
    {
        var s = new UniversalVoiceSegment
        {
            SegmentId = id, SourceId = "src", AudioPath = $"{id}.wav", CacheKey = id,
            AlignmentMethod = method, AlignmentConfidence = 0.9, PitchReliability = reliability,
            AlignmentConfidenceStatus = ConfidenceStatus.Measured
        };
        double t = 0;
        foreach (var p in phonemes)
        {
            s.Phonemes.Add(new PhonemeTimingRecord { Phoneme = p, StartSec = t, EndSec = t + 0.1, Confidence = 0.9 });
            t += 0.1;
        }
        return new UtilityObservation { Segment = s, F0Hz = f0 ?? [] };
    }

    [Fact]
    public void RarePhoneme_ScoresHigherThanFrequent()
    {
        var u = Service.Compute(
            [Obs("a", ["a"]), Obs("b", ["a"]), Obs("c", ["a"]), Obs("d", ["a"]), Obs("e", ["ky"])],
            "talk");
        Assert.Equal(0.5, u["a"].Value!.Value, 6);
        Assert.Equal(1.0, u["e"].Value!.Value, 6);
        Assert.Equal(["rare_phoneme"], u["e"].ReasonCodes);
        Assert.Empty(u["a"].ReasonCodes);
    }

    [Fact]
    public void SpeakersAreSeparated()
    {
        var one = Service.Compute([Obs("a", ["a"])], "talk");
        var many = Service.Compute([Obs("a", ["a"]), Obs("b", ["a"]), Obs("c", ["a"]), Obs("d", ["a"])], "talk");
        Assert.Equal(1.0, one["a"].Value!.Value, 6);
        Assert.Equal(0.5, many["a"].Value!.Value, 6);
    }

    [Fact]
    public void EmptyPhonemes_IsNeutralWithStatus()
    {
        var u = Service.Compute([Obs("a", [])], "talk")["a"];
        Assert.Null(u.Value);
        Assert.Equal(ConfidenceStatus.Unavailable, u.Status);
    }

    [Fact]
    public void EstimatedOrUnknownPhonemes_DoNotCount()
    {
        var u = Service.Compute(
            [Obs("a", ["a"], method: "legacy-uniform-placeholder"), Obs("b", ["zzz"])],
            "talk");
        Assert.All(u.Values, x => Assert.Equal(ConfidenceStatus.Unavailable, x.Status));
        Assert.All(u.Values, x => Assert.Null(x.Value));
    }

    // 6半音=約1.4142倍。bin0は55Hz以上77.78Hz未満、bin-1は55Hz未満。
    // 境界の両側にある2区間は別binなので、どちらも希少度1.0になる。同じbinの2区間は 1/sqrt(2)。
    [Theory]
    [InlineData(54.99, 55.0)]
    [InlineData(77.78, 77.79)]
    public void SingingRangeBoundary_Bin(double below, double above)
    {
        var split = Service.Compute([Obs("x", [], [below]), Obs("y", [], [above])], "singing");
        Assert.Equal(1.0, split["x"].Value!.Value, 6);
        Assert.Equal(1.0, split["y"].Value!.Value, 6);
        var same = Service.Compute([Obs("x", [], [above]), Obs("y", [], [above])], "singing");
        Assert.Equal(1.0 / Math.Sqrt(2), same["x"].Value!.Value, 6);
    }

    [Fact]
    public void InvalidF0_ZeroNegativeNaN_NoRangeBin()
    {
        var u = Service.Compute([Obs("a", [], [0, -5, double.NaN, double.PositiveInfinity])], "singing")["a"];
        Assert.Null(u.Value);
        Assert.Equal(ConfidenceStatus.Unavailable, u.Status);
        var unreliable = Service.Compute([Obs("b", [], [220], reliability: 0)], "singing")["b"];
        Assert.Equal(ConfidenceStatus.Unavailable, unreliable.Status);
    }

    [Fact]
    public void Singing_AveragesPhonemeAndRange_AndUsesOneWhenOtherMissing()
    {
        // 音素 "a" は2区間(希少度 1/sqrt2)、音域binは別(希少度 1.0)。平均は両方の中間。
        var u = Service.Compute([Obs("a", ["a"], [220]), Obs("b", ["a"], [440])], "singing");
        var expected = (1.0 / Math.Sqrt(2) + 1.0) / 2;
        Assert.Equal(expected, u["a"].Value!.Value, 6);
        var onlyRange = Service.Compute([Obs("c", [], [220])], "singing")["c"];
        Assert.Equal(1.0, onlyRange.Value!.Value, 6);
    }

    [Fact]
    public void RangeRarity_CountsSegmentsNotFrames()
    {
        // 長い区間が単独の高いbinを持ち、短い2区間が低いbinを共有する。フレーム数ではなく区間数で数える。
        var longHigh = Obs("a", [], Enumerable.Repeat(440.0, 500).ToArray());
        var shortLowA = Obs("b", [], Enumerable.Repeat(110.0, 50).ToArray());
        var shortLowB = Obs("c", [], Enumerable.Repeat(110.0, 50).ToArray());
        var u = Service.Compute([longHigh, shortLowA, shortLowB], "singing");
        Assert.Equal(1.0, u["a"].Value!.Value, 6);
        Assert.Equal(1.0 / Math.Sqrt(2), u["b"].Value!.Value, 6);
        Assert.True(u["a"].Value > u["b"].Value);
    }

    [Fact]
    public void OrderIndependent()
    {
        var items = new[] { Obs("a", ["a", "ky"]), Obs("b", ["a"]), Obs("c", ["e"]) };
        var first = Service.Compute(items, "talk");
        var second = Service.Compute(items.Reverse(), "talk");
        Assert.Equal(first.Keys.OrderBy(x => x), second.Keys.OrderBy(x => x));
        foreach (var k in first.Keys) Assert.Equal(first[k].Value, second[k].Value);
    }

    [Fact]
    public void InvalidTarget_Throws() => Assert.Throws<ArgumentException>(() => Service.Compute([], "x"));
}
