using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// 判定の入力。話者・用途と、呼び出し側が確定させた事実(ユーザー除外・確定誤話者・音声の可読性)を渡す。
public sealed class TrainingEligibilityInput
{
    public required string SpeakerId { get; init; }
    // talk または singing(TrainingAssessmentVocabulary.Targets)。
    public required string Target { get; init; }
    public IReadOnlySet<string> UserExcludedSegmentIds { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> ConfirmedWrongSpeakerSegmentIds { get; init; } = new HashSet<string>();
    // 音声ファイルが存在し読めるか。未指定(null)のときは可読性を判定せず、読めるものとして扱う。
    public Func<UniversalVoiceSegment, bool>? IsAudioReadable { get; init; }
}

public sealed class TrainingEligibilityDecision
{
    public required string SegmentId { get; init; }
    public required string Eligibility { get; init; }
    // 先頭が主理由、以降が副次理由。eligibleのときは空。
    public required IReadOnlyList<string> ReasonCodes { get; init; }
    public string? PrimaryReason => ReasonCodes.Count > 0 ? ReasonCodes[0] : null;
}

public sealed class TrainingEligibilityResult
{
    public required IReadOnlyList<TrainingEligibilityDecision> Decisions { get; init; }
    public int EligibleCount => Decisions.Count(x => x.Eligibility == "eligible");
    // eligibleが0件のとき true。呼び出し側は「利用可能区間なし」と理由別内訳を表示する。
    public bool HasNoUsableSegments => EligibleCount == 0;
    public IReadOnlyDictionary<string, int> PrimaryReasonCounts =>
        Decisions.Where(x => x.PrimaryReason is not null)
            .GroupBy(x => x.PrimaryReason!)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count());
}

// 話者×用途ごとに区間を eligible / needs_review / excluded に判定し、理由コードを付ける。
// 正本: docs/design/v1-training/contracts-and-weighting.md「2. 使用適格性」。重み計算は含めない。
public sealed class TrainingEligibilityService
{
    // {
    //   責務: [Evaluate: 対象話者×用途の各区間へ適格性と理由コードを決定的に付ける]
    //   処理: [
    //     1: 対象話者の区間と、話者未確定・確定誤話者・ユーザー除外の区間を候補にする
    //     2: 判定順(ユーザー除外→誤話者→音声→用途→alignment)に該当する理由を全て集める
    //     3: 先頭の理由で採否を決め、SegmentId昇順で返す
    //   ]
    //   戻り値: [result: 区間ごとの判定。品質が低いだけでは除外しない]
    // }
    public TrainingEligibilityResult Evaluate(
        IEnumerable<UniversalVoiceSegment> segments, TrainingEligibilityInput input)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SpeakerId))
            throw new ArgumentException("話者IDが空です。", nameof(input));
        if (!TrainingAssessmentVocabulary.Targets.Contains(input.Target))
            throw new ArgumentException($"未対応の用途です: {input.Target}", nameof(input));

        var decisions = segments
            .Where(x => IsCandidate(x, input))
            .OrderBy(x => x.SegmentId, StringComparer.Ordinal)
            .Select(x => Decide(x, input))
            .ToArray();
        return new TrainingEligibilityResult { Decisions = decisions };
    }

    private static bool IsCandidate(UniversalVoiceSegment segment, TrainingEligibilityInput input) =>
        string.IsNullOrWhiteSpace(segment.SpeakerId) ||
        segment.SpeakerId == input.SpeakerId ||
        input.ConfirmedWrongSpeakerSegmentIds.Contains(segment.SegmentId) ||
        input.UserExcludedSegmentIds.Contains(segment.SegmentId);

    private static TrainingEligibilityDecision Decide(
        UniversalVoiceSegment segment, TrainingEligibilityInput input)
    {
        var excluded = new List<string>();
        var review = new List<string>();

        if (input.UserExcludedSegmentIds.Contains(segment.SegmentId)) excluded.Add("user_excluded");
        if (input.ConfirmedWrongSpeakerSegmentIds.Contains(segment.SegmentId)) excluded.Add("wrong_speaker");
        else if (string.IsNullOrWhiteSpace(segment.SpeakerId)) review.Add("speaker_unconfirmed");

        if (input.IsAudioReadable is not null && !input.IsAudioReadable(segment)) excluded.Add("unreadable_audio");
        else if (!HasUsableVoice(segment)) excluded.Add("no_usable_voice");

        if (!MatchesTarget(segment.ContentType, input.Target)) review.Add("wrong_content_type");
        if (input.Target == "singing" && !segment.HasValidSingingTrainingAlignment()) review.Add("alignment_required");

        var reasons = excluded.Concat(review).ToArray();
        var eligibility = excluded.Count > 0 ? "excluded" : review.Count > 0 ? "needs_review" : "eligible";
        return new TrainingEligibilityDecision
        {
            SegmentId = segment.SegmentId, Eligibility = eligibility, ReasonCodes = reasons
        };
    }

    // 有声率が非有限・負なら不正、正なら使える。0 は「有声判定を実測して無かった」ときだけ使えない扱いにする
    // (有声特徴ファイルがあるとき)。ファイルがなければ未測定(補完生成区間など)で、除外の根拠にしない。
    // 低SNRや低品質は理由にしない。
    private static bool HasUsableVoice(UniversalVoiceSegment segment)
    {
        var ratio = segment.VoicedRatio;
        if (!double.IsFinite(ratio) || ratio < 0) return false;
        return ratio > 0 || string.IsNullOrWhiteSpace(segment.VoicingFeaturePath);
    }

    private static bool MatchesTarget(SegmentContentType type, string target) =>
        (target == "talk" && type == SegmentContentType.Speech) ||
        (target == "singing" && type == SegmentContentType.Singing);
}
