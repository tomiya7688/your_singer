using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Tests;

// 話者×用途ごとの区間適格性と理由コードを確認する(重み計算は対象外)。
public sealed class TrainingEligibilityTests
{
    private static readonly TrainingEligibilityService Service = new();

    private static UniversalVoiceSegment Seg(
        string id, string? speaker = "spk_a", SegmentContentType type = SegmentContentType.Speech,
        double voiced = 0.8, bool aligned = false)
    {
        var s = new UniversalVoiceSegment
        {
            SegmentId = id,
            SourceId = "src",
            AudioPath = $"{id}.wav",
            CacheKey = id,
            SpeakerId = speaker,
            ContentType = type,
            VoicedRatio = voiced,
            VoicingFeaturePath = $"{id}-voicing.json"
        };
        if (aligned)
        {
            s.AlignmentMethod = UniversalVoiceSegment.RequiredSingingTrainingAlignmentMethod;
            s.AlignmentConfidence = 0.9;
            s.AlignmentMeasurement = new() { Path = $"measurements/{id}.json", Sha256 = new string('a', 64), AudioSha256 = new string('b', 64) };
            s.Phonemes.Add(new PhonemeTimingRecord { Phoneme = "a", StartSec = 0, EndSec = 0.2, Confidence = 0.9 });
        }
        return s;
    }

    private static TrainingEligibilityInput Input(
        string target = "talk", string[]? excluded = null, string[]? wrong = null,
        Func<UniversalVoiceSegment, bool>? readable = null) => new()
        {
            SpeakerId = "spk_a",
            Target = target,
            UserExcludedSegmentIds = new HashSet<string>(excluded ?? []),
            ConfirmedWrongSpeakerSegmentIds = new HashSet<string>(wrong ?? []),
            IsAudioReadable = readable
        };

    private static TrainingEligibilityDecision One(
        UniversalVoiceSegment seg, TrainingEligibilityInput input) =>
        Assert.Single(Service.Evaluate([seg], input).Decisions);

    [Fact]
    public void WrongSpeaker_IsExcluded()
    {
        var d = One(Seg("s1", "spk_b"), Input(wrong: ["s1"]));
        Assert.Equal("excluded", d.Eligibility);
        Assert.Equal("wrong_speaker", d.PrimaryReason);
    }

    [Fact]
    public void OtherSpeakerNotConfirmedWrong_IsNotEvaluatedForThisSpeaker() =>
        Assert.Empty(Service.Evaluate([Seg("s1", "spk_b")], Input()).Decisions);

    [Fact]
    public void UnknownSpeaker_IsNeedsReview()
    {
        var d = One(Seg("s1", null), Input());
        Assert.Equal("needs_review", d.Eligibility);
        Assert.Equal("speaker_unconfirmed", d.PrimaryReason);
    }

    [Fact]
    public void Silence_IsExcluded()
    {
        foreach (var voiced in new[] { 0.0, double.NaN, -1.0 })
        {
            var d = One(Seg("s1", voiced: voiced), Input());
            Assert.Equal("excluded", d.Eligibility);
            Assert.Equal("no_usable_voice", d.PrimaryReason);
        }
    }

    [Fact]
    public void UnmeasuredVoicing_IsNotTreatedAsSilence()
    {
        // 有声特徴ファイルが無い(補完生成区間など)有声率0は未測定で、除外の根拠にしない。
        var generated = Seg("gen", voiced: 0);
        generated.VoicingFeaturePath = string.Empty;
        Assert.Equal("eligible", One(generated, Input()).Eligibility);
    }

    [Fact]
    public void BrokenWav_IsExcluded()
    {
        var d = One(Seg("s1"), Input(readable: _ => false));
        Assert.Equal("excluded", d.Eligibility);
        Assert.Equal("unreadable_audio", d.PrimaryReason);
    }

    [Fact]
    public void SingingWithoutAlignment_IsNeedsReviewAndOthersContinue()
    {
        var result = Service.Evaluate(
        [
            Seg("a_no_align", type: SegmentContentType.Singing),
            Seg("b_ok", type: SegmentContentType.Singing, aligned: true)
        ], Input("singing"));
        Assert.Equal("needs_review", result.Decisions[0].Eligibility);
        Assert.Equal("alignment_required", result.Decisions[0].PrimaryReason);
        Assert.Equal("eligible", result.Decisions[1].Eligibility);
        Assert.False(result.HasNoUsableSegments);
    }

    [Fact]
    public void LowQualityUsable_StaysEligible()
    {
        var seg = Seg("s1", voiced: 0.02);
        seg.AsrConfidence = 0.1;
        seg.PitchReliability = 0.05;
        var d = One(seg, Input());
        Assert.Equal("eligible", d.Eligibility);
        Assert.Empty(d.ReasonCodes);
    }

    [Fact]
    public void AllNeedsReview_ReportsNoUsableSegments()
    {
        var result = Service.Evaluate(
            [Seg("s1", type: SegmentContentType.Singing), Seg("s2", type: SegmentContentType.Ambiguous)],
            Input("talk"));
        Assert.True(result.HasNoUsableSegments);
        Assert.All(result.Decisions, x => Assert.Equal("needs_review", x.Eligibility));
        Assert.Equal(2, result.PrimaryReasonCounts["wrong_content_type"]);
    }

    [Theory]
    [InlineData("talk", SegmentContentType.Speech)]
    [InlineData("singing", SegmentContentType.Singing)]
    public void UserExcluded_NeverReadopted(string target, SegmentContentType type)
    {
        var d = One(Seg("s1", type: type, aligned: true), Input(target, excluded: ["s1"]));
        Assert.Equal("excluded", d.Eligibility);
        Assert.Equal("user_excluded", d.PrimaryReason);
    }

    [Fact]
    public void ContentMismatch_IsNeedsReviewNotPrimaryForOtherTarget()
    {
        var d = One(Seg("s1", type: SegmentContentType.Singing, aligned: true), Input("talk"));
        Assert.Equal("needs_review", d.Eligibility);
        Assert.Equal(["wrong_content_type"], d.ReasonCodes);
    }

    [Fact]
    public void SecondaryReasonsFollowThePrimary()
    {
        var d = One(Seg("s1", voiced: 0), Input(excluded: ["s1"]));
        Assert.Equal(["user_excluded", "no_usable_voice"], d.ReasonCodes);
    }

    [Fact]
    public void ResultIsOrderedBySegmentIdAndAllReasonsAreInVocabulary()
    {
        var result = Service.Evaluate(
            [Seg("z", null), Seg("a", voiced: 0), Seg("m", type: SegmentContentType.Ambiguous)], Input());
        Assert.Equal(["a", "m", "z"], result.Decisions.Select(x => x.SegmentId));
        Assert.All(result.Decisions.SelectMany(x => x.ReasonCodes),
            x => Assert.Contains(x, TrainingAssessmentVocabulary.ReasonCodes));
    }

    [Fact]
    public void InvalidInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => Service.Evaluate([], new() { SpeakerId = "", Target = "talk" }));
        Assert.Throws<ArgumentException>(() => Service.Evaluate([], new() { SpeakerId = "a", Target = "x" }));
    }
}
