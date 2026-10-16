using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Dataset.Completion;

namespace YourSinger.Process.Tests;

public sealed class SpeakerFeatureEstimationTests
{
    private static CancellationToken Token => TestProject.Token;

    [Fact]
    public async Task OtherReliablePhonesProvideEstimatedSpeakerFeature()
    {
        using var p = new TestProject();
        var reference = p.Segment("reference");
        reference.Phonemes.Clear();
        reference.Phonemes.Add(new()
        {
            Phoneme = "a", StartSec = 0, EndSec = 2, Confidence = 0.95
        });
        await p.SeedAsync(reference);
        var observed = await new TrainingDatasetSnapshotService(p.DatasetRepository)
            .LoadAsync(p.Workspace, Token);

        var generated = Generated();
        var correction = SpeakerFeatureEstimator.Apply(
            observed, generated, ["k"], 0.93);

        Assert.Equal(CorrectionApplicationStatus.Applied, correction.ApplicationStatus);
        Assert.Equal(CorrectionState.Estimated, correction.State);
        Assert.Equal(SpeakerFeatureEstimator.Method, correction.Method);
        Assert.Equal(0.93, correction.Confidence, 3);
        Assert.Equal(new[] { 1.0, 0.0 }, generated.SpeakerEmbedding);
        Assert.Equal(generated.SpeakerEmbedding, correction.ResultValues);
        Assert.Contains("観測会話1件", correction.Reason!);

        var saved = Assert.Single((await p.DatasetRepository.LoadAsync(p.Workspace, Token))!.Segments);
        Assert.Empty(saved.SpeakerEmbedding);
    }

    [Fact]
    public async Task TargetPhoneOnlyReferencesDoNotPretendToEstimateSpeakerFeature()
    {
        using var p = new TestProject();
        var reference = p.Segment("reference");
        reference.Phonemes.Clear();
        reference.Phonemes.Add(new()
        {
            Phoneme = "k", StartSec = 0, EndSec = 2, Confidence = 0.95
        });
        await p.SeedAsync(reference);
        var observed = await new TrainingDatasetSnapshotService(p.DatasetRepository)
            .LoadAsync(p.Workspace, Token);

        var generated = Generated();
        var correction = SpeakerFeatureEstimator.Apply(
            observed, generated, ["k"], 0.95);

        Assert.Equal(CorrectionApplicationStatus.Deferred, correction.ApplicationStatus);
        Assert.Empty(generated.SpeakerEmbedding);
        Assert.Empty(correction.ResultValues);
        Assert.Contains("補助対象以外", correction.Reason!);
    }

    [Fact]
    public async Task GeneratedReferencesAreNeverUsedRecursively()
    {
        using var p = new TestProject();
        var observed = p.Segment("observed");
        observed.Phonemes.Clear();
        observed.Phonemes.Add(new()
        {
            Phoneme = "k", StartSec = 0, EndSec = 2, Confidence = 0.95
        });
        await p.SeedAsync(observed);
        var snapshot = await new TrainingDatasetSnapshotService(p.DatasetRepository)
            .LoadAsync(p.Workspace, Token);
        snapshot.Segments.Add(new UniversalVoiceSegment
        {
            SegmentId = "old-generated",
            SourceId = "generated:old",
            SpeakerId = "spk_a",
            ContentType = SegmentContentType.Speech,
            AudioPath = "generated.wav",
            CacheKey = "generated",
            AsrConfidence = 1,
            AlignmentConfidence = 1,
            Phonemes =
            [
                new()
                {
                    Phoneme = "a", StartSec = 0, EndSec = 2, Confidence = 1
                }
            ],
            SpeakerEmbedding = [0, 1]
        });

        var target = Generated();
        var correction = SpeakerFeatureEstimator.Apply(snapshot, target, ["k"], 0.95);

        Assert.Equal(CorrectionApplicationStatus.Deferred, correction.ApplicationStatus);
        Assert.Empty(target.SpeakerEmbedding);
    }

    [Fact]
    public async Task InvalidObservedEmbeddingDefersInsteadOfPublishingInvalidNumbers()
    {
        using var p = new TestProject();
        var reference = p.Segment("reference");
        reference.Phonemes.Clear();
        reference.Phonemes.Add(new()
        {
            Phoneme = "a", StartSec = 0, EndSec = 2, Confidence = 0.95
        });
        await p.SeedAsync(reference);
        var observed = await new TrainingDatasetSnapshotService(p.DatasetRepository)
            .LoadAsync(p.Workspace, Token);
        observed.Segments[0].SpeakerEmbedding.Clear();
        observed.Segments[0].SpeakerEmbedding.Add(double.NaN);

        var generated = Generated();
        var correction = SpeakerFeatureEstimator.Apply(
            observed, generated, ["k"], 0.95);

        Assert.Equal(CorrectionApplicationStatus.Deferred, correction.ApplicationStatus);
        Assert.Empty(generated.SpeakerEmbedding);
    }

    [Fact]
    public void NonGeneratedTargetIsNeverOverwritten()
    {
        var dataset = new UniversalVoiceDatasetRecord { SchemaVersion = "1", StageVersion = "test" };
        var observed = new UniversalVoiceSegment
        {
            SegmentId = "observed", SourceId = "raw", SpeakerId = "spk_a",
            ContentType = SegmentContentType.Speech, AudioPath = "observed.wav",
            CacheKey = "observed", SpeakerEmbedding = [0.3, 0.7]
        };
        var result = SpeakerFeatureEstimator.Apply(dataset, observed, ["k"], 0.94);
        Assert.Equal(CorrectionApplicationStatus.Deferred, result.ApplicationStatus);
        Assert.Equal([0.3, 0.7], observed.SpeakerEmbedding);
    }

    [Fact]
    public async Task SingingAndUnreliableSpeechAreNotUsedToEstimateTalkFeature()
    {
        using var p = new TestProject();
        var singing = p.Segment("singing", type: SegmentContentType.Singing);
        singing.Phonemes.Clear();
        singing.Phonemes.Add(new() { Phoneme = "a", StartSec = 0, EndSec = 2, Confidence = 0.95 });
        var unreliable = p.Segment("unreliable", confidence: 0.5);
        await p.SeedAsync(singing, unreliable);
        var snapshot = await new TrainingDatasetSnapshotService(p.DatasetRepository)
            .LoadAsync(p.Workspace, Token);

        var target = Generated();
        var result = SpeakerFeatureEstimator.Apply(snapshot, target, ["k"], 0.93);
        Assert.Equal(CorrectionApplicationStatus.Deferred, result.ApplicationStatus);
        Assert.Empty(target.SpeakerEmbedding);
    }

    [Fact]
    public void InvalidVerificationScoreCannotPublishEstimate()
    {
        var dataset = new UniversalVoiceDatasetRecord { SchemaVersion = "1", StageVersion = "test" };
        var target = Generated();
        var result = SpeakerFeatureEstimator.Apply(dataset, target, ["k"], double.NaN);
        Assert.Equal(CorrectionApplicationStatus.Deferred, result.ApplicationStatus);
        Assert.Empty(target.SpeakerEmbedding);
    }

    private static UniversalVoiceSegment Generated() => new()
    {
        SegmentId = "generated-test",
        SourceId = "generated:test",
        SpeakerId = "spk_a",
        ContentType = SegmentContentType.Speech,
        AudioPath = "features/generated.wav",
        CacheKey = "generated"
    };
}
