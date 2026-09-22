using System.Globalization;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Speaker;
using YourSinger.Process.Processing.Talk;

namespace YourSinger.Process.Tests;

public sealed class EditingRegressionTests
{
    private static CancellationToken Token => TestProject.Token;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualExclusionAlwaysReachesTraining(bool correction)
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("keep"), p.Segment("exclude"));
        await new SpeakerOverrideService(p.Speakers).ExcludeSegmentAsync(p.Workspace, "exclude", true, Token);
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk, AutoCorrectionEnabled = correction }], Token);
        Assert.Equal(new[] { "keep" }, Assert.Single(batch.Jobs).SegmentIds);
        // 修正は投影で適用し、保存済みの観測結果は消さない。
        Assert.Contains((await p.DatasetRepository.LoadAsync(p.Workspace, Token))!.Segments, x => x.SegmentId == "exclude");
    }

    [Fact]
    public async Task RestoringExcludedSegmentRestoresTrainingInput()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"), p.Segment("b"));
        var edits = new SpeakerOverrideService(p.Speakers);
        await edits.ExcludeSegmentAsync(p.Workspace, "b", true, Token);
        await edits.ExcludeSegmentAsync(p.Workspace, "b", false, Token);
        var view = await p.Correction().BuildAsync(p.Workspace, false, Token);
        Assert.Equal(new[] { "a", "b" }, view.Segments.Select(x => x.SegmentId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MergeRecalculatesDurationAndPreservesCrossFileAssignments()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("long", duration: 10),
            p.Segment("short", "spk_b", duration: 2, source: "src_b"));
        var edits = new SpeakerOverrideService(p.Speakers);
        await edits.MergeSpeakersAsync(p.Workspace, "spk_a", ["spk_b"], Token);
        var merged = (await p.Speakers.LoadAsync(p.Workspace, Token))!;
        Assert.Equal(12, Assert.Single(merged.Speakers).TotalDurationSec);
        Assert.All(merged.Assignments, x => Assert.Equal("spk_a", x.SpeakerId));
        await edits.ExcludeSegmentAsync(p.Workspace, "long", true, Token);
        var speaker = Assert.Single((await p.Speakers.LoadAsync(p.Workspace, Token))!.Speakers);
        Assert.Equal(2, speaker.UsableDurationSec);
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk }], Token);
        Assert.Equal(new[] { "short" }, Assert.Single(batch.Jobs).SegmentIds);
    }

    [Fact]
    public async Task InvalidMergeDoesNotOverwriteSavedAnalysis()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"), p.Segment("b", "spk_b"));
        var path = Path.Combine(p.Workspace.MetadataPath, "speakers.json");
        var before = await File.ReadAllBytesAsync(path, Token);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new SpeakerOverrideService(p.Speakers)
            .MergeSpeakersAsync(p.Workspace, "spk_a", ["spk_b", "missing"], Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
    }

    [Fact]
    public async Task ClassificationEditsReachTalkAndSingingBuilders()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("to_singing"), p.Segment("to_talk", type: SegmentContentType.Singing));
        var edits = new ContentClassificationOverrideService(p.Classifications);
        await edits.SetContentTypeAsync(p.Workspace, "to_singing", SegmentContentType.Singing, Token);
        await edits.SetContentTypeAsync(p.Workspace, "to_talk", SegmentContentType.Speech, Token);
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Both, AutoCorrectionEnabled = false }], Token);
        var talk = Assert.Single(batch.Jobs, x => x.Target == TrainingTarget.Talk);
        var singing = Assert.Single(batch.Jobs, x => x.Target == TrainingTarget.Singing);
        Assert.Equal(new[] { "to_talk" }, talk.SegmentIds);
        Assert.Equal(new[] { "to_singing" }, singing.SegmentIds);
        var talkData = await new StyleBertVits2DatasetBuilder(p.DatasetRepository)
            .BuildAsync(p.Workspace, talk, "話者あ", Token);
        var singingData = await new DiffSingerDatasetBuilder(p.DatasetRepository)
            .BuildAsync(p.Workspace, singing, Token);
        Assert.Equal("to_talk", Assert.Single(talkData.Items).SegmentId);
        Assert.Equal("to_singing", Assert.Single(singingData.Items).SegmentId);
        Assert.True(File.Exists(talkData.Items[0].AudioPath));
    }

    [Fact]
    public async Task FingerprintChangesWhenTranscriptOrFeaturesChange()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var first = await p.Correction().BuildAsync(p.Workspace, false, Token);
        var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        dataset.Segments[0].Transcript = "修正した文章";
        await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
        var second = await p.Correction().BuildAsync(p.Workspace, false, Token);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        dataset.Segments[0].MeanF0Hz = 440;
        await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
        var third = await p.Correction().BuildAsync(p.Workspace, false, Token);
        Assert.NotEqual(second.Fingerprint, third.Fingerprint);
    }

    [Fact]
    public async Task FingerprintIsIndependentOfCultureAndSegmentOrder()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a", confidence: 0.5), p.Segment("b"));
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            var first = await p.Correction().BuildAsync(p.Workspace, true, Token);
            var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
            dataset.Segments.Reverse();
            await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = await p.Correction().BuildAsync(p.Workspace, true, Token);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(TrainingTarget.Talk, 1)]
    [InlineData(TrainingTarget.Singing, 1)]
    [InlineData(TrainingTarget.Both, 2)]
    public async Task TargetSelectionCreatesSeparateJobs(TrainingTarget target, int expected)
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("talk"), p.Segment("song", type: SegmentContentType.Singing),
            p.Segment("uncertain", type: SegmentContentType.Ambiguous));
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = target, AutoCorrectionEnabled = false }], Token);
        Assert.Equal(expected, batch.Jobs.Count);
        Assert.All(batch.Jobs, x => Assert.DoesNotContain("uncertain", x.SegmentIds));
        var restored = await p.Jobs.LoadBatchAsync(p.Workspace, batch.BatchId, Token);
        Assert.NotNull(restored);
        Assert.Equal(batch.Jobs.Select(x => x.DatasetFingerprint), restored.Jobs.Select(x => x.DatasetFingerprint));
        var retry = await p.Planner().RecreateBatchAsync(p.Workspace, batch, Token);
        Assert.NotEqual(batch.BatchId, retry.BatchId);
        Assert.Equal(batch.Jobs.Select(x => x.Target), retry.Jobs.Select(x => x.Target));
        Assert.All(retry.Jobs, x => Assert.False(x.AutoCorrectionEnabled));
    }

    [Fact]
    public async Task AutomaticRejectionIsDisabledButManualEditsRemainWhenOff()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("weak", confidence: 0.1), p.Segment("strong"));
        var on = await p.Correction().BuildAsync(p.Workspace, true, Token);
        var off = await p.Correction().BuildAsync(p.Workspace, false, Token);
        Assert.DoesNotContain(on.Segments, x => x.SegmentId == "weak");
        Assert.Contains(off.Segments, x => x.SegmentId == "weak");
        Assert.NotEqual(on.Fingerprint, off.Fingerprint);
    }

    [Fact]
    public async Task MultipleSpeakersAreNeverMixedInAJob()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"), p.Segment("b", "spk_b"));
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk },
             new() { SpeakerId = "spk_b", Target = TrainingTarget.Talk }], Token);
        Assert.Equal(new[] { "a" }, Assert.Single(batch.Jobs, x => x.SpeakerId == "spk_a").SegmentIds);
        Assert.Equal(new[] { "b" }, Assert.Single(batch.Jobs, x => x.SpeakerId == "spk_b").SegmentIds);
        Assert.NotEqual(batch.Jobs[0].DatasetFingerprint, batch.Jobs[1].DatasetFingerprint);
    }
}
