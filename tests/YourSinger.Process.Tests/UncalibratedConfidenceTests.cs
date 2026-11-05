using YourSinger.Data.Models;

namespace YourSinger.Process.Tests;

// 未校正・未計測の信頼度(0)と、実測された低信頼値を区別して自動補正が判定することを確認する。
public sealed class UncalibratedConfidenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(CorrectedDatasetView View, IReadOnlyList<CorrectionRecord> Corrections)> RunAsync(
        UniversalVoiceSegment segment, bool correctionEnabled)
    {
        using var p = new TestProject();
        await p.SeedAsync(segment);
        var view = await p.Correction().BuildAsync(p.Workspace, correctionEnabled, Token);
        return (view, view.AppliedCorrections);
    }

    [Fact]
    public async Task MfaSuccessWithUncalibratedZeroIsNotRejected()
    {
        var segment = new TestProject().Segment("seg_mfa");
        segment.AlignmentConfidence = 0;
        segment.AlignmentConfidenceStatus = ConfidenceStatus.Uncalibrated;
        var (view, corrections) = await RunAsync(segment, true);
        Assert.Single(view.Segments);
        Assert.DoesNotContain(corrections, x => x.State == CorrectionState.Rejected);
    }

    [Fact]
    public async Task LegacyMfaZeroWithoutStatusIsTreatedAsUncalibrated()
    {
        var segment = new TestProject().Segment("seg_legacy");
        segment.AlignmentConfidence = 0;
        var (view, _) = await RunAsync(segment, true);
        Assert.Single(view.Segments);
    }

    [Fact]
    public async Task ExplicitlyMeasuredZeroAlignmentIsRejected()
    {
        var segment = new TestProject().Segment("seg_measured_zero");
        segment.AlignmentConfidence = 0;
        segment.AlignmentConfidenceStatus = ConfidenceStatus.Measured;
        var (view, corrections) = await RunAsync(segment, true);
        Assert.Empty(view.Segments);
        Assert.Contains(corrections, x => x.State == CorrectionState.Rejected && x.SegmentId == "seg_measured_zero");
    }

    [Fact]
    public async Task MeasuredLowAsrConfidenceIsRejected()
    {
        var segment = new TestProject().Segment("seg_low_asr");
        segment.AsrConfidence = 0.2;
        segment.AsrConfidenceStatus = ConfidenceStatus.Measured;
        var (view, _) = await RunAsync(segment, true);
        Assert.Empty(view.Segments);
    }

    [Fact]
    public async Task UserCorrectedTranscriptWithUnavailableAsrIsNotRejected()
    {
        var segment = new TestProject().Segment("seg_override");
        segment.AsrConfidence = 0;
        segment.AsrConfidenceStatus = ConfidenceStatus.Unavailable;
        var (view, _) = await RunAsync(segment, true);
        Assert.Single(view.Segments);
    }

    [Fact]
    public async Task CorrectionDisabledKeepsEverySegment()
    {
        var segment = new TestProject().Segment("seg_off");
        segment.AlignmentConfidence = 0;
        segment.AlignmentConfidenceStatus = ConfidenceStatus.Measured;
        var (view, _) = await RunAsync(segment, false);
        Assert.Single(view.Segments);
    }
}
