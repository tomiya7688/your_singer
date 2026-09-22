using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Speaker;

namespace YourSinger.Process.Tests;

public sealed class PhonemeCandidateTests
{
    private static CancellationToken Token => TestProject.Token;
    // この代替は要求構築の検証用。音声生成の品質やモデル実行を検証しない。
    private sealed class UnusedWorker : IPhonemeCandidateWorker
    {
        public int Calls { get; private set; }
        public Task<JsonElement> GenerateAsync(object payload, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("試験では生成を実行しない");
        }
    }

    [Fact]
    public async Task MissingPhonesAreCountedForSelectedSpeakerOnly()
    {
        using var p = new TestProject();
        var a = p.Segment("a", duration: 4);
        var b = p.Segment("b", "spk_b", duration: 4);
        b.Phonemes.Add(new() { Phoneme = "k", StartSec = 0, EndSec = 0.1, Confidence = 0.95 });
        await p.SeedAsync(a, b);
        var plan = await new PhonemeCandidateService(p.DatasetRepository, new UnusedWorker()).PlanAsync(p.Workspace, "spk_a", Token);
        Assert.Contains("k", plan.MissingPhonemes);
        Assert.DoesNotContain("a", plan.MissingPhonemes);
        Assert.Equal("a", plan.ReferenceSegmentId);
    }

    [Fact]
    public async Task SingingIsNotUsedAsSpeechReference()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("song", type: SegmentContentType.Singing, duration: 4));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PhonemeCandidateService(p.DatasetRepository, new UnusedWorker()).PlanAsync(p.Workspace, "spk_a", Token));
    }

    [Fact]
    public async Task ManualExclusionRemovesReferenceCandidate()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("ref", duration: 4));
        await new SpeakerOverrideService(p.Speakers).ExcludeSegmentAsync(p.Workspace, "ref", true, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PhonemeCandidateService(p.DatasetRepository, new UnusedWorker()).PlanAsync(p.Workspace, "spk_a", Token));
    }

    [Fact]
    public async Task LowConfidenceDataIsNotAReference()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("ref", duration: 4, confidence: 0.2));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PhonemeCandidateService(p.DatasetRepository, new UnusedWorker()).PlanAsync(p.Workspace, "spk_a", Token));
    }

    [Fact]
    public async Task ReferenceMustBeLongEnoughForCloneInput()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("ref", duration: 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PhonemeCandidateService(p.DatasetRepository, new UnusedWorker()).PlanAsync(p.Workspace, "spk_a", Token));
    }

    [Fact]
    public async Task ChangedTranscriptPreventsGenerationFromConfirmedOldPlan()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("ref", duration: 4));
        var worker = new UnusedWorker();
        var service = new PhonemeCandidateService(p.DatasetRepository, worker);
        var plan = await service.PlanAsync(p.Workspace, "spk_a", Token);
        var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        dataset.Segments[0].Transcript = "変更した文章";
        await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(p.Workspace, plan, Token));
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public async Task ChangedReferenceBytesPreventGeneration()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("ref", duration: 4));
        var worker = new UnusedWorker();
        var service = new PhonemeCandidateService(p.DatasetRepository, worker);
        var plan = await service.PlanAsync(p.Workspace, "spk_a", Token);
        await File.AppendAllTextAsync(plan.ReferenceAudioPath, "changed", Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(p.Workspace, plan, Token));
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public async Task PlanningDoesNotModifyObservedDataOrCreateTrainingJobs()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("ref", duration: 4));
        var path = Path.Combine(p.Workspace.MetadataPath, "universal-voice-dataset.json");
        var before = await File.ReadAllBytesAsync(path, Token);
        _ = await new PhonemeCandidateService(p.DatasetRepository, new UnusedWorker()).PlanAsync(p.Workspace, "spk_a", Token);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.MetadataPath, "training-jobs")));
        Assert.False(File.Exists(Path.Combine(p.Workspace.MetadataPath, "auto-correction.json")));
    }
}
