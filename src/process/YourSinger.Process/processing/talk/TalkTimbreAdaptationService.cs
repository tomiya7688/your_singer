using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Talk;

public sealed record TalkTimbreReference(string SegmentId, string SourceId, string AudioPath);

public sealed record TalkTimbreSkip(string SegmentId, string Reason);

// トークモデルへ渡す、歌唱由来の声質参照(経路T1)。歌唱音声は教師データではなく、スタイルベクトルを作る参照にだけ使う。
public sealed class TalkTimbreAdaptationInput
{
    public const string StyleName = "singing_reference";

    public required string SpeakerId { get; init; }
    // 同一話者の歌唱区間(SegmentId昇順)。発話教師が0件でも作れる。
    public required IReadOnlyList<TalkTimbreReference> References { get; init; }
    public required IReadOnlyList<TalkTimbreSkip> Skipped { get; init; }
    // ジョブに残す利用記録。参照が無い・無効のときは Used=false と理由を持つ。
    public required SharedFeatureUsageEntry Usage { get; init; }
    public bool HasReferences => References.Count > 0;
}

// 同一話者の歌唱区間から、トーク向けの声質参照を選ぶ。別話者・話者不明・除外・確認待ち・発話区間は含めない。
// 正本: docs/design/v1-training/routing-and-cross-domain.md「実現経路（設計）」T1。
public sealed class TalkTimbreAdaptationService
{
    private const string Feature = "style_vector_from_singing_audio";
    private readonly UsageRoutingService _routing = new();
    private readonly TrainingEligibilityService _eligibility = new();

    // {
    //   責務: [Build: 同一話者の歌唱区間から、スタイルベクトルを作る参照音声を選ぶ]
    //   処理: [
    //     1: 無効なら参照を選ばず、理由付きの利用記録にする
    //     2: routing で歌唱の primary かつ対象話者の区間を選び、除外(ユーザー除外・読めない音声・有声なし)を除く
    //     3: 参照が1件も無ければ Used=false と理由を記録する
    //   ]
    //   戻り値: [input: 参照・除いた区間と理由・利用記録]
    // }
    public TalkTimbreAdaptationInput Build(
        IEnumerable<UniversalVoiceSegment> segments,
        ContentClassificationRecord classification,
        string speakerId,
        bool enabled = true,
        IReadOnlySet<string>? userExcludedSegmentIds = null,
        Func<UniversalVoiceSegment, bool>? isAudioReadable = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(classification);
        if (string.IsNullOrWhiteSpace(speakerId)) throw new ArgumentException("話者IDが空です。", nameof(speakerId));
        if (!enabled) return Result(speakerId, [], [], "歌唱由来の声質参照が無効に設定されています。");

        var all = segments.Where(x => x.SpeakerId == speakerId).OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToArray();
        var excluded = userExcludedSegmentIds ?? new HashSet<string>();
        var routing = _routing.Route(all, classification, excluded).Decisions.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);
        var eligibility = _eligibility.Evaluate(all, new TrainingEligibilityInput
        {
            SpeakerId = speakerId, Target = "talk", UserExcludedSegmentIds = excluded, IsAudioReadable = isAudioReadable
        }).Decisions.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);

        var references = new List<TalkTimbreReference>();
        var skipped = new List<TalkTimbreSkip>();
        foreach (var segment in all)
        {
            var decision = routing[segment.SegmentId];
            if (decision.Singing.Role != UsageRole.Primary)
            {
                // 歌唱でない区間は参照にしない(発話は教師、曖昧・除外は混ぜない)。
                if (decision.Talk.Role != UsageRole.Primary)
                    skipped.Add(new(segment.SegmentId, decision.Talk.ReasonCodes.FirstOrDefault() ?? decision.Talk.Role));
                continue;
            }
            var check = eligibility[segment.SegmentId];
            if (check.Eligibility == "excluded")
                skipped.Add(new(segment.SegmentId, check.PrimaryReason ?? "excluded"));
            else
                references.Add(new(segment.SegmentId, segment.SourceId, segment.AudioPath));
        }
        return Result(speakerId, references, skipped,
            references.Count == 0 ? "この話者に使える歌唱区間がありません。" : null);
    }

    private static TalkTimbreAdaptationInput Result(
        string speakerId, List<TalkTimbreReference> references, List<TalkTimbreSkip> skipped, string? unusedReason) => new()
    {
        SpeakerId = speakerId, References = references, Skipped = skipped,
        Usage = new SharedFeatureUsageEntry
        {
            Feature = Feature, Domain = SharedFeatureDomain.Singing, Support = SharedFeatureSupport.Supported,
            Used = unusedReason is null,
            Reason = unusedReason ?? $"歌唱区間 {references.Count} 件の音声から作るスタイルベクトルを、トークモデルのスタイル「{TalkTimbreAdaptationInput.StyleName}」として追加します(教師データには使いません)。"
        }
    };
}
