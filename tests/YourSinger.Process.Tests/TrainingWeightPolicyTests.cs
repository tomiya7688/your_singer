using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Tests;

// v1の決定的な重み計算(training-weight-v1)を確認する。式の最適性ではなく、回帰の基準。
public sealed class TrainingWeightPolicyTests
{
    private static readonly TrainingWeightPolicy Policy = new();

    private static TrainingMeasurement Measured(double v) => new() { Value = v, Status = ConfidenceStatus.Measured };

    private static TrainingWeightInput Item(
        string id,
        double? quality,
        double? utility,
        string eligibility = "eligible",
        string qualityStatus = ConfidenceStatus.Measured)
    {
        var components = new Dictionary<string, TrainingMeasurement>();
        if (quality is not null)
            components["asr_confidence"] = new() { Value = quality, Status = qualityStatus };
        return new TrainingWeightInput
        {
            SegmentId = id, Eligibility = eligibility, QualityComponents = components,
            Utility = utility is null
                ? new() { Value = null, Status = ConfidenceStatus.Unavailable, ReasonCodes = [] }
                : new() { Value = utility, Status = ConfidenceStatus.Measured, ReasonCodes = [] }
        };
    }

    [Fact]
    public void Q1U0_Raw1_0() =>
        Assert.Equal(1.0, Policy.Compute([Item("a", 1, 0)])["a"].RawWeight, 9);

    [Fact]
    public void Q0U1_Raw0_6125_Positive()
    {
        var r = Policy.Compute([Item("a", 0, 1)])["a"];
        Assert.Equal(0.35 * 1.75, r.RawWeight, 9);
        Assert.True(r.TrainingWeight > 0);
    }

    [Fact]
    public void HigherQuality_GetsHigherWeight_ForSameUtility()
    {
        var r = Policy.Compute([Item("hi", 0.9, 0.5), Item("lo", 0.2, 0.5)]);
        Assert.True(r["hi"].TrainingWeight > r["lo"].TrainingWeight);
        Assert.True(r["lo"].TrainingWeight > 0);
    }

    [Fact]
    public void AllUnknown_UsesNeutralButKeepsStatus()
    {
        var r = Policy.Compute([Item("a", null, null)])["a"];
        Assert.Null(r.Quality.Value);
        Assert.Equal(ConfidenceStatus.Unavailable, r.Quality.Status);
        Assert.Equal((0.35 + 0.65 * 0.5) * (1 + 0.75 * 0.5), r.RawWeight, 9);
    }

    [Fact]
    public void MeasuredZero_IsDifferentFromUnknown()
    {
        var zero = Policy.Compute([Item("a", 0, 0)])["a"];
        var unknown = Policy.Compute([Item("a", null, 0)])["a"];
        Assert.Equal(0.0, zero.Quality.Value);
        Assert.Equal(ConfidenceStatus.Measured, zero.Quality.Status);
        Assert.Null(unknown.Quality.Value);
        Assert.True(unknown.RawWeight > zero.RawWeight);
    }

    [Fact]
    public void UncalibratedComponent_IsNotUsedAsZero()
    {
        var r = Policy.Compute([Item("a", 0, 0, qualityStatus: ConfidenceStatus.Uncalibrated)])["a"];
        Assert.Null(r.Quality.Value);
        Assert.Equal(ConfidenceStatus.Unavailable, r.Quality.Status);
    }

    [Fact]
    public void QualityIsMeanOfMeasuredOnly()
    {
        var input = Item("a", 0.8, 0);
        var components = new Dictionary<string, TrainingMeasurement>(input.QualityComponents)
        {
            ["alignment_confidence"] = Measured(0.4),
            ["f0_reliability"] = new() { Value = null, Status = ConfidenceStatus.LegacyUnknown }
        };
        var r = Policy.Compute([new TrainingWeightInput
        {
            SegmentId = "a", Eligibility = "eligible", QualityComponents = components, Utility = input.Utility
        }])["a"];
        Assert.Equal(0.6, r.Quality.Value!.Value, 9);
    }

    [Fact]
    public void ZeroEligible_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Policy.Compute([]));
        Assert.Throws<InvalidOperationException>(() =>
            Policy.Compute([Item("a", 1, 1, "needs_review"), Item("b", 1, 1, "excluded")]));
    }

    [Fact]
    public void NonEligible_GetWeightZero()
    {
        var r = Policy.Compute([Item("a", 1, 1), Item("b", 1, 1, "needs_review"), Item("c", 1, 1, "excluded")]);
        Assert.Equal(0.0, r["b"].TrainingWeight);
        Assert.Equal(0.0, r["c"].TrainingWeight);
        Assert.True(r["a"].TrainingWeight > 0);
    }

    [Fact]
    public void Shuffle_SameWeights()
    {
        var items = Enumerable.Range(0, 8)
            .Select(i => Item($"s{i}", i / 8.0, (7 - i) / 8.0)).ToArray();
        var first = Policy.Compute(items);
        var second = Policy.Compute(items.Reverse());
        foreach (var k in first.Keys)
        {
            Assert.Equal(first[k].RawWeight, second[k].RawWeight);
            Assert.Equal(first[k].TrainingWeight, second[k].TrainingWeight);
        }
    }

    [Fact]
    public void Weights_AreClampedToRange()
    {
        var items = Enumerable.Range(0, 20).Select(i => Item($"lo{i}", 0, 0)).Append(Item("hi", 1, 1)).ToArray();
        var r = Policy.Compute(items);
        Assert.Equal(TrainingWeightPolicy.MaxWeight, r["hi"].TrainingWeight);
        var items2 = Enumerable.Range(0, 20).Select(i => Item($"hi{i}", 1, 1)).Append(Item("lo", 0, 0)).ToArray();
        Assert.Equal(TrainingWeightPolicy.MinWeight, Policy.Compute(items2)["lo"].TrainingWeight);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void NonFinite_Rejected(double bad)
    {
        Assert.Throws<ArgumentException>(() => Policy.Compute([Item("a", bad, 0)]));
        Assert.Throws<ArgumentException>(() => Policy.Compute([Item("a", 0.5, bad)]));
    }

    [Fact]
    public void DuplicateIds_Rejected() =>
        Assert.Throws<ArgumentException>(() => Policy.Compute([Item("a", 1, 1), Item("a", 1, 1)]));

    [Fact]
    public void SeparateInputs_AutoCorrectionOnOff_DoNotShareState()
    {
        // 補正ON/OFFの入力は別々に計算でき、互いの結果に影響しない。
        var on = Policy.Compute([Item("a", 0.9, 0.5), Item("b", 0.8, 0.5)]);
        var off = Policy.Compute([Item("a", 0.3, 0.5), Item("b", 0.8, 0.5)]);
        Assert.NotEqual(on["a"].TrainingWeight, off["a"].TrainingWeight);
        Assert.Equal(on["a"].RawWeight, Policy.Compute([Item("a", 0.9, 0.5), Item("b", 0.8, 0.5)])["a"].RawWeight);
    }

    [Fact]
    public void ResultFitsTheAssessmentContract()
    {
        var inputs = new[] { Item("a", 0.9, 0.7), Item("b", null, null), Item("c", 0.2, 0.1, "needs_review") };
        var results = Policy.Compute(inputs);
        var record = new TrainingAssessmentRecord
        {
            SchemaVersion = TrainingAssessmentRecord.CurrentSchemaVersion,
            PolicyVersion = TrainingWeightPolicy.PolicyVersion, JobId = "job1", SpeakerId = "spk",
            Target = "talk", SourceDatasetFingerprint = "fp",
            Items = inputs.OrderBy(x => x.SegmentId, StringComparer.Ordinal).Select(x => new TrainingAssessmentItem
            {
                SegmentId = x.SegmentId, Eligibility = x.Eligibility, ReasonCodes = [],
                Quality = results[x.SegmentId].Quality, Utility = x.Utility,
                RawWeight = results[x.SegmentId].RawWeight, TrainingWeight = results[x.SegmentId].TrainingWeight
            }).ToList()
        };
        TrainingAssessmentValidator.Validate(record);
    }
}
