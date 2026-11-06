using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// 区間の使われ方(D-04)。教師データの採否(eligibility)とは別の軸。
public static class UsageRole
{
    public const string Primary = "primary";
    public const string FeatureOnly = "feature_only";
    public const string Review = "review";
    public const string Excluded = "excluded";
}

public static class UsageRoutingReason
{
    public const string SpeakerReference = "speaker_reference";
    public const string AmbiguousContent = "ambiguous_content";
    public const string UserExcluded = "user_excluded";
    public const string SpeakerUnconfirmed = "speaker_unconfirmed";
    public const string ClassificationMissing = "classification_missing";
}

public sealed class UsageRoleAssignment
{
    // primary / feature_only / review / excluded(UsageRole)。
    public required string Role { get; init; }
    // 経路上の状態。primary と feature_only は eligible、review は needs_review、excluded は excluded。
    public required string Status { get; init; }
    public required IReadOnlyList<string> ReasonCodes { get; init; }
}

public sealed class UsageRoutingDecision
{
    public required string SegmentId { get; init; }
    public string? SpeakerId { get; init; }
    public required UsageRoleAssignment Talk { get; init; }
    public required UsageRoleAssignment Singing { get; init; }
    // automatic または manual(手動修正が最優先)。
    public required string ClassificationOrigin { get; init; }
    // 手動修正のとき、修正前の自動ラベル。自動なら null。
    public SegmentContentType? OriginalContentType { get; init; }
    public double? ClassificationConfidence { get; init; }
    // measured / legacy_unknown / unavailable / invalid(ConfidenceStatus)。
    public required string ClassificationConfidenceStatus { get; init; }
}

public sealed class UsageRoutingResult
{
    public required IReadOnlyList<UsageRoutingDecision> Decisions { get; init; }

    // 対象話者の決定だけを返す。他話者は混ぜない。
    public IReadOnlyList<UsageRoutingDecision> ForSpeaker(string speakerId) =>
        Decisions.Where(x => x.SpeakerId == speakerId).ToArray();

    // 用途・役割ごとの区間数。feature_only の量を後続の計画へ渡すために使う。
    public int Count(string target, string role) => Decisions.Count(x =>
        (target == "talk" ? x.Talk : x.Singing).Role == role);
}

// 会話・歌唱が混在した素材を、事前に選り分けずに用途別の役割へ振り分ける。
// 正本: docs/design/v1-training/routing-and-cross-domain.md「提案するrouting契約」。
// 分類モデルや既存の UsageDatasetRouter(TalkSegmentIds 等)は変更しない。
public sealed class UsageRoutingService
{
    // {
    //   責務: [Route: 区間ごとに talk / singing の役割と理由を決定的に決める]
    //   処理: [
    //     1: ユーザー除外を最優先にし、次に分類(手動修正が自動に優先)を使う
    //     2: speech/singing は対応用途の primary、別用途は話者が確定していれば feature_only にする
    //     3: ambiguous と分類なしは両用途で review にし、教師データへ入れない
    //   ]
    //   戻り値: [result: SegmentId昇順の決定。信頼度0を別人扱い・削除の理由にしない]
    // }
    public UsageRoutingResult Route(
        IEnumerable<UniversalVoiceSegment> segments,
        ContentClassificationRecord classification,
        IReadOnlySet<string>? userExcludedSegmentIds = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(classification);
        var excluded = userExcludedSegmentIds ?? new HashSet<string>();
        var byId = classification.Classifications.GroupBy(x => x.SegmentId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.Ordinal);
        var overrides = classification.Overrides.GroupBy(x => x.SegmentId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var decisions = segments.OrderBy(x => x.SegmentId, StringComparer.Ordinal)
            .Select(segment => Decide(segment, byId.GetValueOrDefault(segment.SegmentId),
                overrides.GetValueOrDefault(segment.SegmentId), excluded.Contains(segment.SegmentId)))
            .ToArray();
        return new UsageRoutingResult { Decisions = decisions };
    }

    private static UsageRoutingDecision Decide(
        UniversalVoiceSegment segment,
        SegmentContentClassification? classification,
        ContentClassificationOverride? change,
        bool userExcluded)
    {
        var manual = classification?.UserOverridden == true || change is not null;
        var (confidence, status) = Confidence(classification, manual);
        var speakerKnown = !string.IsNullOrWhiteSpace(segment.SpeakerId);
        UsageRoleAssignment talk, singing;
        if (userExcluded)
        {
            talk = singing = Assign(UsageRole.Excluded, UsageRoutingReason.UserExcluded);
        }
        else if (classification is null)
        {
            talk = singing = Assign(UsageRole.Review, UsageRoutingReason.ClassificationMissing);
        }
        else
        {
            (talk, singing) = classification.ContentType switch
            {
                SegmentContentType.Speech => (Assign(UsageRole.Primary), Other(speakerKnown)),
                SegmentContentType.Singing => (Other(speakerKnown), Assign(UsageRole.Primary)),
                _ => (Assign(UsageRole.Review, UsageRoutingReason.AmbiguousContent),
                    Assign(UsageRole.Review, UsageRoutingReason.AmbiguousContent))
            };
        }
        return new UsageRoutingDecision
        {
            SegmentId = segment.SegmentId, SpeakerId = segment.SpeakerId, Talk = talk, Singing = singing,
            ClassificationOrigin = manual ? "manual" : "automatic",
            OriginalContentType = manual ? change?.OriginalType : null,
            ClassificationConfidence = confidence, ClassificationConfidenceStatus = status
        };
    }

    // 対応しない用途は、話者が確定している区間だけ共有話者特徴(feature_only)にする。教師データにはしない。
    private static UsageRoleAssignment Other(bool speakerKnown) => speakerKnown
        ? Assign(UsageRole.FeatureOnly, UsageRoutingReason.SpeakerReference)
        : Assign(UsageRole.Excluded, UsageRoutingReason.SpeakerUnconfirmed);

    private static UsageRoleAssignment Assign(string role, params string[] reasons) => new()
    {
        Role = role, ReasonCodes = reasons,
        Status = role switch
        {
            UsageRole.Primary or UsageRole.FeatureOnly => "eligible",
            UsageRole.Review => "needs_review",
            _ => "excluded"
        }
    };

    // 自動分類の信頼度は0を「未計測」として扱い(旧データに計測有無の区別が無い)、手動修正には信頼度が無い。
    private static (double? Value, string Status) Confidence(SegmentContentClassification? classification, bool manual)
    {
        if (classification is null || manual) return (null, ConfidenceStatus.Unavailable);
        var value = classification.Confidence;
        if (!double.IsFinite(value) || value is < 0 or > 1) return (null, ConfidenceStatus.Invalid);
        return value > 0 ? (value, ConfidenceStatus.Measured) : (null, ConfidenceStatus.LegacyUnknown);
    }
}
