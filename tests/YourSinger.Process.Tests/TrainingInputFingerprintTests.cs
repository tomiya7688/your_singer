using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 適格性→Utility→重みの組み立てと、学習入力fingerprintの再現性・変化を確認する。
public sealed class TrainingInputFingerprintTests
{
    private static readonly TrainingAssessmentBuilder Builder = new();

    private static UniversalVoiceSegment Seg(
        string id,
        string speaker = "spk_a",
        SegmentContentType type = SegmentContentType.Speech,
        string[]? phonemes = null,
        double asr = 0.9,
        string asrStatus = ConfidenceStatus.Measured)
    {
        var s = new UniversalVoiceSegment
        {
            SegmentId = id, SourceId = "src", AudioPath = $"{id}.wav", CacheKey = id, SpeakerId = speaker,
            ContentType = type, VoicedRatio = 0.8, AsrConfidence = asr, AsrConfidenceStatus = asrStatus,
            AlignmentMethod = UniversalVoiceSegment.RequiredSingingTrainingAlignmentMethod,
            AlignmentConfidence = 0.8, AlignmentConfidenceStatus = ConfidenceStatus.Measured, PitchReliability = 0.9,
            AlignmentMeasurement = new() { Path = $"measurements/{id}.json", Sha256 = new string('a', 64), AudioSha256 = new string('b', 64) }
        };
        double t = 0;
        foreach (var p in phonemes ?? ["a"])
        {
            s.Phonemes.Add(new PhonemeTimingRecord { Phoneme = p, StartSec = t, EndSec = t + 0.1, Confidence = 0.9 });
            t += 0.1;
        }
        return s;
    }

    private static TrainingAssessmentBuildInput Input(string target = "talk", string[]? excluded = null) => new()
    {
        JobId = "job1", SpeakerId = "spk_a", Target = target, SourceDatasetFingerprint = "snapshot-1",
        UserExcludedSegmentIds = new HashSet<string>(excluded ?? []),
        F0Provider = _ => [220.0]
    };

    private static TrainingJobRecord Job(TrainingMode mode = TrainingMode.Auto, string preset = "natural") => new()
    {
        JobId = "job1", SpeakerId = "spk_a", Target = TrainingTarget.Talk, DatasetFingerprint = "snapshot-1",
        RequestedTexture = new TrainingTextureRequest { Preset = preset }, RequestedTrainingMode = mode,
        ResolvedTrainingMode = mode == TrainingMode.Auto ? null : mode,
        Initialization = mode == TrainingMode.FromScratch
            ? new TrainingInitialization { Type = TrainingJobVocabulary.RandomInitialization }
            : null
    };

    private static string Fingerprint(
        TrainingAssessmentRecord record,
        TrainingJobRecord? job = null,
        string revision = "rev1",
        string? config = null) =>
        TrainingInputFingerprint.Compute(new TrainingInputFingerprintInput
        {
            DatasetFingerprint = "snapshot-1", ContractFingerprint = TrainingJobContractFingerprint.Compute(job ?? Job()),
            Backend = "diffsinger", BackendRevision = revision, TrainerConfigSha256 = config, Assessment = record
        });

    [Fact]
    public void Builder_ProducesAValidAssessmentWithWeights()
    {
        var record = Builder.Build(
            [Seg("a", phonemes: ["a"]), Seg("b", phonemes: ["a"]), Seg("c", phonemes: ["ky"]), Seg("x", "spk_a", asr: 0.9)],
            Input(excluded: ["x"]));
        TrainingAssessmentValidator.Validate(record);
        Assert.Equal(["a", "b", "c", "x"], record.Items.Select(x => x.SegmentId));
        Assert.Equal("excluded", record.Items[3].Eligibility);
        Assert.Equal(0.0, record.Items[3].TrainingWeight);
        Assert.True(record.Items[2].TrainingWeight > record.Items[0].TrainingWeight);
    }

    [Fact]
    public void Builder_NoEligible_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            Builder.Build([Seg("a", type: SegmentContentType.Singing)], Input("talk")));

    [Fact]
    public void Builder_UncalibratedAsr_IsNotUsedAsQualityZero()
    {
        var item = Builder.Build([Seg("a", asr: 0, asrStatus: ConfidenceStatus.Uncalibrated)], Input()).Items.Single();
        Assert.Equal(ConfidenceStatus.Uncalibrated, item.Quality.Components["asr_confidence"].Status);
        Assert.Null(item.Quality.Components["asr_confidence"].Value);
    }

    [Fact]
    public void Builder_SingingUsesF0Range_AndKeepsF0ReliabilityUnused()
    {
        var item = Builder.Build([Seg("a", type: SegmentContentType.Singing)], Input("singing")).Items.Single();
        Assert.Equal(ConfidenceStatus.Measured, item.Utility.Status);
        Assert.Null(item.Quality.Components["f0_reliability"].Value);
    }

    [Fact]
    public void Fingerprint_Fixed_AndOrderIndependent()
    {
        var segments = new[] { Seg("a"), Seg("b", phonemes: ["ky"]), Seg("c") };
        var first = Fingerprint(Builder.Build(segments, Input()));
        var second = Fingerprint(Builder.Build(segments.Reverse(), Input()));
        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Fingerprint_ChangesOnWeightOrMode()
    {
        var record = Builder.Build([Seg("a"), Seg("b", phonemes: ["ky"])], Input());
        var baseline = Fingerprint(record);
        var changedWeight = Builder.Build([Seg("a"), Seg("b", phonemes: ["ky"], asr: 0.2)], Input());
        Assert.NotEqual(baseline, Fingerprint(changedWeight));
        Assert.NotEqual(baseline, Fingerprint(record, Job(TrainingMode.FromScratch)));
    }

    [Fact]
    public void Fingerprint_ChangesOnTextureBackendAndConfig()
    {
        var record = Builder.Build([Seg("a")], Input());
        var baseline = Fingerprint(record);
        Assert.NotEqual(baseline, Fingerprint(record, Job(preset: "bright")));
        Assert.NotEqual(baseline, Fingerprint(record, revision: "rev2"));
        Assert.NotEqual(baseline, Fingerprint(record, config: new string('a', 64)));
    }

    [Fact]
    public void Fingerprint_ChangesOnUserExclusionAndPolicyVersion()
    {
        var segments = new[] { Seg("a"), Seg("b") };
        var baseline = Fingerprint(Builder.Build(segments, Input()));
        Assert.NotEqual(baseline, Fingerprint(Builder.Build(segments, Input(excluded: ["b"]))));
        var record = Builder.Build(segments, Input());
        var otherPolicy = new TrainingAssessmentRecord
        {
            SchemaVersion = record.SchemaVersion, PolicyVersion = "training-weight-v2", JobId = record.JobId,
            SpeakerId = record.SpeakerId, Target = record.Target, SourceDatasetFingerprint = record.SourceDatasetFingerprint,
            Items = record.Items
        };
        Assert.NotEqual(baseline, Fingerprint(otherPolicy));
    }

    [Fact]
    public void Fingerprint_RejectsBlankInputsAndUnorderedItems()
    {
        var record = Builder.Build([Seg("a"), Seg("b")], Input());
        Assert.Throws<ArgumentException>(() => TrainingInputFingerprint.Compute(new TrainingInputFingerprintInput
        {
            DatasetFingerprint = " ", ContractFingerprint = "c", Backend = "b", BackendRevision = "r", Assessment = record
        }));
        var reversed = new TrainingAssessmentRecord
        {
            SchemaVersion = record.SchemaVersion, PolicyVersion = record.PolicyVersion, JobId = record.JobId,
            SpeakerId = record.SpeakerId, Target = record.Target, SourceDatasetFingerprint = record.SourceDatasetFingerprint,
            Items = [.. record.Items.AsEnumerable().Reverse()]
        };
        Assert.Throws<ArgumentException>(() => Fingerprint(reversed));
    }
}
