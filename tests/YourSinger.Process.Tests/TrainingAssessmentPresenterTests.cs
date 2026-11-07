using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Tests;

// { 責務: [TrainingAssessmentPresenterTests: 4区分の件数・未計測を0%と見せない表示・理由の日本語化を確認する] }
public sealed class TrainingAssessmentPresenterTests
{
    private static TrainingAssessmentItem Item(string id, string eligibility, double? q, string qStatus, double weight,
        params string[] reasons) => new()
        {
            SegmentId = id,
            Eligibility = eligibility,
            ReasonCodes = [.. reasons],
            Quality = new TrainingQualityAssessment { Value = q, Status = qStatus, Components = [] },
            Utility = new TrainingUtilityAssessment { Value = null, Status = ConfidenceStatus.Unavailable, ReasonCodes = [] },
            RawWeight = weight,
            TrainingWeight = weight
        };

    private static TrainingAssessmentRecord Record(params TrainingAssessmentItem[] items) => new()
    {
        SchemaVersion = "1",
        PolicyVersion = "p",
        JobId = "j",
        SpeakerId = "s",
        Target = "singing",
        SourceDatasetFingerprint = "fp",
        Items = [.. items]
    };

    [Fact]
    public void ItemsAreCountedIntoFourCategoriesAndLowQualityIsNotExcluded()
    {
        var counts = TrainingAssessmentPresenter.Count(Record(
            Item("a", "eligible", 0.9, ConfidenceStatus.Measured, 1.2),
            Item("b", "eligible", 0.2, ConfidenceStatus.Measured, 0.4),
            Item("c", "eligible", null, ConfidenceStatus.Unavailable, 0.8),
            Item("d", "needs_review", 0.9, ConfidenceStatus.Measured, 0, "speaker_unconfirmed"),
            Item("e", "excluded", 0.9, ConfidenceStatus.Measured, 0, "user_excluded")));
        Assert.Equal(new TrainingAssessmentCounts("singing", 2, 1, 1, 1), counts);
        Assert.Equal("歌唱: 採用 2件 / 低品質だが採用 1件 / 要確認 1件 / 除外 1件", TrainingAssessmentPresenter.Summarize(counts));
    }

    [Fact]
    public void UnmeasuredValuesAreNeverShownAsZeroPercentAndReasonsAreJapanese()
    {
        var unmeasured = TrainingAssessmentPresenter.Describe(Item("c", "eligible", null, ConfidenceStatus.Unavailable, 0.8));
        Assert.Contains("品質 未計測", unmeasured);
        Assert.DoesNotContain("0%", unmeasured);
        Assert.Contains("品質 未校正", TrainingAssessmentPresenter.Describe(Item("u", "eligible", 0.7, ConfidenceStatus.Uncalibrated, 1)));
        var low = TrainingAssessmentPresenter.Describe(Item("b", "eligible", 0.2, ConfidenceStatus.Measured, 0.4, "rare_phoneme"));
        Assert.Contains("低品質だが採用", low);
        Assert.Contains("品質 20%", low);
        Assert.Contains("重み 0.40", low);
        Assert.Contains("理由: 珍しい音素を含む", low);
    }
}
