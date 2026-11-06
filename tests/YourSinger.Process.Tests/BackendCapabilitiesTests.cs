using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 固定版Backendの共有特徴の受入能力表と、Adapter契約(話者・用途・revisionの検証、未対応の記録)を確認する。
public sealed class BackendCapabilitiesTests
{
    private static SharedSpeakerFeatureSet Features(string speaker = "spk_a")
    {
        var segment = new UniversalVoiceSegment
        {
            SegmentId = "a", SourceId = "s", AudioPath = "a.wav", CacheKey = "a", SpeakerId = speaker,
            ContentType = SegmentContentType.Speech, MeanF0Hz = 200, F0StdDevHz = 10, PitchReliability = 0.9,
            StyleProsody = new StyleProsodyRecord { SpeakingRate = 5, PitchRangeSemitones = 4, EnergyVariation = 0.1, PauseRatio = 0.2 }
        };
        segment.SpeakerEmbedding.AddRange([1.0, 0.0]);
        var routing = new UsageRoutingService().Route([segment], new ContentClassificationRecord
        {
            StageVersion = "t",
            Classifications = [new SegmentContentClassification { SegmentId = "a", ContentType = SegmentContentType.Speech, Confidence = 0.8 }]
        });
        return new SharedSpeakerFeatureService().Build([segment], routing, speaker);
    }

    private static SharedFeatureAdapterRequest Request(
        BackendCapability capability, string? revision = null, string? target = null, string speaker = "spk_a",
        string featuresSpeaker = "spk_a") => new()
    {
        Backend = capability.Backend, Revision = revision ?? capability.Revision, Target = target ?? capability.Target,
        SpeakerId = speaker, Features = Features(featuresSpeaker)
    };

    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative)))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new FileNotFoundException(relative), relative);
    }

    [Fact]
    public void CapabilityTable_MatchesDocument()
    {
        var lines = File.ReadAllLines(RepositoryFile("docs/design/v1-training/routing-and-cross-domain.md"));
        var rows = lines.Where(x => x.StartsWith("| diffsinger") || x.StartsWith("| style-bert-vits2"))
            .Select(x => x.Split('|', StringSplitOptions.TrimEntries)).Select(x => (Backend: x[1], Feature: x[2], Support: x[3]))
            .ToHashSet();
        var expected = new[] { BackendCapabilities.DiffSinger, BackendCapabilities.StyleBertVits2 }
            .SelectMany(c => c.Features.Select(f => (c.Backend, f.Feature, f.Support))).ToHashSet();
        Assert.Equal(expected, rows);
        Assert.Equal(6, expected.Count);
    }

    [Fact]
    public void FixedRevisionsMatchTheTrainingBackends()
    {
        Assert.Equal(TrainingBackendCatalog.SingingRevision, BackendCapabilities.DiffSinger.Revision);
        Assert.Equal(TrainingBackendCatalog.TalkRevision, BackendCapabilities.StyleBertVits2.Revision);
        Assert.All(BackendCapabilities.FeatureNames, name =>
        {
            Assert.Contains(BackendCapabilities.DiffSinger.Features, f => f.Feature == name);
            Assert.Contains(BackendCapabilities.StyleBertVits2.Features, f => f.Feature == name);
        });
    }

    [Fact]
    public void Unsupported_ReportsZeroUseWithReasons()
    {
        foreach (var capability in new[] { BackendCapabilities.DiffSinger, BackendCapabilities.StyleBertVits2 })
        {
            var report = BackendCapabilities.Evaluate(Request(capability));
            Assert.False(report.AnyUsed);
            Assert.NotEmpty(report.Entries);
            Assert.All(report.Entries, x =>
            {
                Assert.Equal(SharedFeatureSupport.Unsupported, x.Support);
                Assert.False(x.Used);
                Assert.False(string.IsNullOrWhiteSpace(x.Reason));
            });
        }
    }

    [Fact]
    public void VoiceIdMismatch_Rejected() =>
        Assert.Throws<ArgumentException>(() =>
            BackendCapabilities.Evaluate(Request(BackendCapabilities.StyleBertVits2, speaker: "spk_b", featuresSpeaker: "spk_a")));

    [Fact]
    public void TargetMismatch_Rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            BackendCapabilities.Evaluate(Request(BackendCapabilities.StyleBertVits2, target: "singing")));
        Assert.Throws<ArgumentException>(() =>
            BackendCapabilities.Evaluate(Request(BackendCapabilities.DiffSinger, target: "talk")));
    }

    [Fact]
    public void RevisionMismatch_AndUnknownBackend_Rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            BackendCapabilities.Evaluate(Request(BackendCapabilities.DiffSinger, revision: "other")));
        Assert.Throws<ArgumentException>(() => BackendCapabilities.For("unknown", "x"));
    }

    [Fact]
    public async Task Unsupported_ShownInJobRecord()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("a"));
        var batch = await project.Planner().CreateBatchAsync(project.Workspace,
            [new SpeakerTrainingSelection { SpeakerId = "spk_a", Target = TrainingTarget.Talk, AutoCorrectionEnabled = false }],
            TestProject.Token);
        var job = Assert.Single(batch.Jobs);
        var report = BackendCapabilities.Evaluate(Request(BackendCapabilities.StyleBertVits2));
        job.SharedFeatureUsage = [.. report.Entries];
        await project.Jobs.SaveBatchAsync(project.Workspace, batch, TestProject.Token);

        var loaded = Assert.Single((await project.Jobs.LoadBatchAsync(project.Workspace, batch.BatchId, TestProject.Token))!.Jobs);
        Assert.NotNull(loaded.SharedFeatureUsage);
        Assert.All(loaded.SharedFeatureUsage!, x =>
        {
            Assert.Equal(SharedFeatureSupport.Unsupported, x.Support);
            Assert.False(x.Used);
        });
    }
}
