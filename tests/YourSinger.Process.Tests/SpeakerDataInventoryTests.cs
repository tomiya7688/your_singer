using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 話者ごとの発話教師・歌唱教師・声質参照の実効量が別軸で集計され、測れない値を0にしないことを確認する。
public sealed class SpeakerDataInventoryTests
{
    private static readonly SpeakerDataInventoryService Service = new();

    private static UniversalVoiceSegment Seg(
        string id,
        string? speaker = "spk_a",
        string source = "src_a",
        string? cache = null,
        bool aligned = false,
        string[]? phonemes = null,
        bool embedding = true)
    {
        var s = new UniversalVoiceSegment
        {
            SegmentId = id, SourceId = source, AudioPath = $"{id}.wav", CacheKey = cache ?? id, SpeakerId = speaker,
            VoicedRatio = 0.8, VoicingFeaturePath = $"{id}-voicing.json", Transcript = "あ",
            AsrConfidence = 0.9, AsrConfidenceStatus = ConfidenceStatus.Measured,
            PitchReliability = 0.9, AlignmentMethod = aligned
                ? UniversalVoiceSegment.RequiredSingingTrainingAlignmentMethod : "legacy-uniform-placeholder",
            AlignmentConfidence = 0.9, AlignmentConfidenceStatus = ConfidenceStatus.Measured
        };
        if (embedding) s.SpeakerEmbedding.AddRange([1.0, 0.0]);
        double t = 0;
        foreach (var p in phonemes ?? ["a"])
        {
            s.Phonemes.Add(new PhonemeTimingRecord { Phoneme = p, StartSec = t, EndSec = t + 0.1, Confidence = 0.9 });
            t += 0.1;
        }
        return s;
    }

    private static SpeakerDataInventory Build(
        Dictionary<string, SegmentContentType> types,
        UniversalVoiceSegment[] segments,
        Func<UniversalVoiceSegment, double?>? duration = null,
        string[]? excluded = null,
        string fingerprint = "fp1",
        string speaker = "spk_a")
    {
        // 補正後の区間は分類結果と同じ ContentType を持つ前提で、テストの区間にも反映する。
        foreach (var segment in segments)
            if (types.TryGetValue(segment.SegmentId, out var type)) segment.ContentType = type;
        return BuildCore(types, segments, duration, excluded, fingerprint, speaker);
    }

    private static SpeakerDataInventory BuildCore(
        Dictionary<string, SegmentContentType> types,
        UniversalVoiceSegment[] segments,
        Func<UniversalVoiceSegment, double?>? duration,
        string[]? excluded,
        string fingerprint,
        string speaker) => Service.Build(segments, new SpeakerDataInventoryInput
    {
        SpeakerId = speaker, DatasetFingerprint = fingerprint,
        Classification = new ContentClassificationRecord
        {
            StageVersion = "t",
            Classifications = [.. types.Select(x => new SegmentContentClassification { SegmentId = x.Key, ContentType = x.Value, Confidence = 0.8 })]
        },
        UserExcludedSegmentIds = new HashSet<string>(excluded ?? []),
        DurationSeconds = duration ?? (_ => 10.0),
        F0Provider = _ => [110.0, 220.0, 440.0]
    });

    [Fact]
    public void SingingOnly50Minutes_StillHasIdentityReference()
    {
        var segments = Enumerable.Range(0, 5).Select(i => Seg($"song{i}", aligned: true)).ToArray();
        var inv = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Singing), segments, _ => 600.0);
        Assert.Equal(0, inv.Speech.EligibleSeconds);
        Assert.Equal(3000, inv.Singing.EligibleSeconds);
        Assert.True(inv.IdentityReference.EligibleSeconds > 0);
        Assert.Contains("speech_teacher_absent", inv.DerivedTags);
        Assert.Contains("has_singing_teacher", inv.DerivedTags);
        Assert.Contains("has_identity_reference", inv.DerivedTags);
    }

    [Fact]
    public void SpeechOnly_StillHasIdentityReference()
    {
        var segments = new[] { Seg("a"), Seg("b") };
        var inv = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Speech), segments);
        Assert.Equal(20, inv.Speech.EligibleSeconds);
        Assert.Equal(0, inv.Singing.EligibleSeconds);
        Assert.Equal(20, inv.IdentityReference.EligibleSeconds);
        Assert.Contains("singing_teacher_absent", inv.DerivedTags);
    }

    [Fact]
    public void DuplicateRecordings_AreNotCountedAsIndependent()
    {
        var segments = new[] { Seg("a", cache: "same"), Seg("b", cache: "same"), Seg("c", cache: "other", source: "src_b") };
        var inv = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Speech), segments);
        Assert.Equal(30, inv.Speech.EligibleSeconds);
        Assert.Equal(2, inv.Speech.IndependentUtterances);
        Assert.Equal(2, inv.Speech.DistinctRecordings);
    }

    [Fact]
    public void UserExcluded_AndOtherSpeakers_AreNotCounted()
    {
        var segments = new[] { Seg("a"), Seg("b"), Seg("other", "spk_b") };
        var inv = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Speech), segments, excluded: ["b"]);
        Assert.Equal(10, inv.Speech.EligibleSeconds);
        Assert.Equal(10, inv.IdentityReference.EligibleSeconds);
    }

    [Fact]
    public void UnmeasuredDuration_IsNotTreatedAsZeroSecondsConfirmed()
    {
        var segments = new[] { Seg("a"), Seg("b") };
        var inv = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Speech), segments,
            duration: x => x.SegmentId == "a" ? 10.0 : null);
        Assert.Equal(10, inv.Speech.EligibleSeconds);
        Assert.Equal("partially_measured", inv.MeasurementStatus["duration"]);
        var none = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Speech), segments, duration: _ => null);
        Assert.Equal(ConfidenceStatus.Unavailable, none.MeasurementStatus["duration"]);
    }

    [Fact]
    public void SingingWithoutAlignment_KeepsRawAndIdentityButNotTeacher()
    {
        var segments = new[] { Seg("ok", aligned: true), Seg("noalign") };
        var inv = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Singing), segments);
        Assert.Equal(20, inv.Singing.RawSeconds);
        Assert.Equal(10, inv.Singing.EligibleSeconds);
        Assert.Equal(20, inv.IdentityReference.EligibleSeconds);
        Assert.Equal(1, inv.Singing.IndependentPhrases);
    }

    [Fact]
    public void AllAmbiguous_HasNoTeacherAndNoIdentityReference()
    {
        var segments = new[] { Seg("a"), Seg("b") };
        var inv = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Ambiguous), segments);
        Assert.Equal(0, inv.Speech.EligibleSeconds + inv.Singing.EligibleSeconds + inv.IdentityReference.EligibleSeconds);
        Assert.Equal(ConfidenceStatus.Unavailable, inv.MeasurementStatus["speaker_identity"]);
    }

    [Fact]
    public void CoverageAndMeasurementStatus_AreReported()
    {
        var segments = new[] { Seg("a", phonemes: ["a", "ky"], aligned: true), Seg("b", embedding: false) };
        var inv = Build(segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Speech), segments);
        // 推定(placeholder)の alignment を持つ "b" の音素は数えず、"a" の2音素だけを数える。
        Assert.Equal(2, inv.Speech.PhonemeCoverageCount);
        Assert.Equal("partially_measured", inv.MeasurementStatus["speaker_identity"]);
        Assert.Equal(2, inv.Speech.TranscriptUsableCount);
    }

    [Fact]
    public void PitchBins_AreCountedFromMeasuredF0()
    {
        var segments = new[] { Seg("a", aligned: true) };
        var inv = Build(new() { ["a"] = SegmentContentType.Singing }, segments);
        Assert.Equal(3, inv.Singing.PitchBinsObserved);
        Assert.Equal(ConfidenceStatus.Measured, inv.MeasurementStatus["singing_f0"]);
    }

    [Fact]
    public void Revision_ChangesWithSourceOverrideAndRules_AndIsStable()
    {
        var segments = new[] { Seg("a"), Seg("b") };
        var types = segments.ToDictionary(x => x.SegmentId, _ => SegmentContentType.Speech);
        var first = Build(types, segments);
        Assert.Equal(first.InventoryRevision, Build(types, [.. segments.Reverse()]).InventoryRevision);
        Assert.NotEqual(first.InventoryRevision, Build(types, segments, fingerprint: "fp2").InventoryRevision);
        Assert.NotEqual(first.InventoryRevision, Build(types, segments, excluded: ["b"]).InventoryRevision);
        Assert.Matches("^[0-9a-f]{64}$", first.InventoryRevision);
    }

    [Fact]
    public void InvalidInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => Build([], [], speaker: " "));
        Assert.Throws<ArgumentException>(() => Build([], [], fingerprint: " "));
    }

    [Fact]
    public async Task WavDurationReader_ReadsHeaderAndReturnsNullWhenUnreadable()
    {
        using var project = new TestProject();
        var segment = project.Segment("a");
        await project.SeedAsync(segment);
        var seconds = WavDurationReader.Read(project.Workspace, segment);
        Assert.NotNull(seconds);
        Assert.True(seconds > 0);
        Assert.Null(WavDurationReader.Read(project.Workspace, project.Segment("missing")));
        Assert.Null(WavDurationReader.Read(project.Workspace, new UniversalVoiceSegment
        {
            SegmentId = "x", SourceId = "s", CacheKey = "c", AudioPath = "../../outside.wav"
        }));
    }
}
