using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Dataset.Completion;

namespace YourSinger.Process.Tests;

public sealed class TranscriptCorrectionTests
{
    private static CancellationToken Token => TestProject.Token;

    [Fact]
    public async Task CorrectionOnAppliesConsensusOnlyToTrainingProjection()
    {
        using var p = new TestProject();
        var observed = p.Segment("weak", confidence: 0.50);
        observed.Transcript = "かき";
        observed.Phonemes.Clear();
        observed.Phonemes.Add(new() { Phoneme = "k", StartSec = 0, EndSec = 1, Confidence = 0.50 });
        observed.Phonemes.Add(new() { Phoneme = "i", StartSec = 1, EndSec = 2, Confidence = 0.50 });
        await p.SeedAsync(observed);

        var service = new TranscriptCorrectionService(
            (_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                review_version = TranscriptCorrectionService.ReviewVersion,
                candidate_transcript = "かぎ",
                candidate_phonemes = new[] { "k", "a", "g", "i" },
                confidence = 0.82,
                duration_sec = 2.0,
                changed = true,
                machine_passed = true,
                reasons = Array.Empty<string>()
            })),
            () => true);

        var correction = new AutoCorrectionService(
            p.DatasetRepository,
            new AutoCorrectionRepository(),
            service);

        var view = await correction.BuildAsync(p.Workspace, true, Token);
        var projected = Assert.Single(view.Segments);
        Assert.Equal("かぎ", projected.Transcript);
        Assert.Equal(0.82, projected.AsrConfidence, 3);
        Assert.Equal(new[] { "k", "a", "g", "i" }, projected.Phonemes.Select(x => x.Phoneme));
        Assert.Equal(2.0, projected.Phonemes[^1].EndSec, 3);

        var applied = Assert.Single(view.AppliedCorrections, x =>
            x.Method == TranscriptCorrectionService.ReviewVersion &&
            x.ApplicationStatus == CorrectionApplicationStatus.Applied);
        Assert.Equal("かき", applied.OriginalValue);
        Assert.Equal("かぎ", applied.CorrectedValue);

        var original = Assert.Single((await p.DatasetRepository.LoadAsync(p.Workspace, Token))!.Segments);
        Assert.Equal("かき", original.Transcript);
        Assert.Equal(new[] { "k", "i" }, original.Phonemes.Select(x => x.Phoneme));
    }

    [Fact]
    public async Task CorrectionOffDoesNotInvokeReviewer()
    {
        using var p = new TestProject();
        var observed = p.Segment("weak", confidence: 0.50);
        observed.Transcript = "元";
        await p.SeedAsync(observed);
        var calls = 0;
        var service = new TranscriptCorrectionService(
            (_, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("呼ばれてはいけません");
            },
            () => true);
        var correction = new AutoCorrectionService(
            p.DatasetRepository, new AutoCorrectionRepository(), service);

        var view = await correction.BuildAsync(p.Workspace, false, Token);
        Assert.Equal(0, calls);
        Assert.Equal("元", Assert.Single(view.Segments).Transcript);
        Assert.DoesNotContain(view.AppliedCorrections,
            x => x.Method == TranscriptCorrectionService.ReviewVersion);
    }

    [Fact]
    public async Task FailedConsensusIsDeferredAndKeepsObservedTranscript()
    {
        using var p = new TestProject();
        var observed = p.Segment("weak", confidence: 0.50);
        observed.Transcript = "観測";
        await p.SeedAsync(observed);

        var service = new TranscriptCorrectionService(
            (_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                review_version = TranscriptCorrectionService.ReviewVersion,
                candidate_transcript = "候補",
                candidate_phonemes = new[] { "k", "o" },
                confidence = 0.70,
                duration_sec = 2.0,
                changed = true,
                machine_passed = false,
                reasons = new[] { "再認識2方式の音素列が一致しません。" }
            })),
            () => true);
        var correction = new AutoCorrectionService(
            p.DatasetRepository, new AutoCorrectionRepository(), service);

        var view = await correction.BuildAsync(p.Workspace, true, Token);
        Assert.Equal("観測", Assert.Single(view.Segments).Transcript);
        var deferred = Assert.Single(view.AppliedCorrections, x =>
            x.Method == TranscriptCorrectionService.ReviewVersion);
        Assert.Equal(CorrectionApplicationStatus.Deferred, deferred.ApplicationStatus);
        Assert.Contains("一致しません", deferred.Reason);
    }

    [Fact]
    public async Task ManualTranscriptOverrideIsNeverAutoCorrected()
    {
        using var p = new TestProject();
        var observed = p.Segment("weak", confidence: 0.50);
        observed.Transcript = "手動";
        await p.SeedAsync(observed);
        var data = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        data.Overrides.Add(new()
        {
            SegmentId = "weak",
            Field = "transcript",
            Value = "手動"
        });
        await p.DatasetRepository.SaveAsync(p.Workspace, data, Token);

        var calls = 0;
        var service = new TranscriptCorrectionService(
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            },
            () => true);
        var correction = new AutoCorrectionService(
            p.DatasetRepository, new AutoCorrectionRepository(), service);

        var view = await correction.BuildAsync(p.Workspace, true, Token);
        Assert.Equal(0, calls);
        Assert.Equal("手動", Assert.Single(view.Segments).Transcript);
    }

    [Fact]
    public async Task MissingCorrectionRuntimeIsTrackedInsteadOfFailingBuild()
    {
        using var p = new TestProject();
        var observed = p.Segment("weak", confidence: 0.50);
        observed.Transcript = "観測";
        await p.SeedAsync(observed);

        var service = new TranscriptCorrectionService(
            (_, _, _) => throw new InvalidOperationException("呼ばれてはいけません"),
            () => false);
        var correction = new AutoCorrectionService(
            p.DatasetRepository, new AutoCorrectionRepository(), service);

        var view = await correction.BuildAsync(p.Workspace, true, Token);
        Assert.Equal("観測", Assert.Single(view.Segments).Transcript);
        var deferred = Assert.Single(view.AppliedCorrections, x =>
            x.Method == TranscriptCorrectionService.ReviewVersion);
        Assert.Equal(CorrectionApplicationStatus.Deferred, deferred.ApplicationStatus);
        Assert.Contains("未準備", deferred.Reason);
    }
}
