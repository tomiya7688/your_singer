using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Talk;

// { 責務: [EmotionStyleGroup: 感情ごとのスタイル作成に使う発話区間の組] }
public sealed record EmotionStyleGroup(string Emotion, IReadOnlyList<string> SegmentIds, IReadOnlyList<string> AudioPaths);

// { 責務: [EmotionStyleSkip: スタイルを作らない感情と日本語の理由] }
public sealed record EmotionStyleSkip(string Emotion, int SegmentCount, string Reason);

// { 責務: [EmotionStylePlan: 感情別スタイルの作成計画(作れる感情・作れない感情・ラベル無しの件数)] }
public sealed record EmotionStylePlan(
    IReadOnlyList<EmotionStyleGroup> Styles, IReadOnlyList<EmotionStyleSkip> Skipped, int UnlabeledSegmentCount);

// {
//   責務: [EmotionStylePlanner: 感情ラベル付きの発話区間を感情ごとにまとめ、スタイル作成に使える感情を決める]
//   処理: [1: 同一話者の発話の教師区間(除外・読めない音声を除く)だけを対象にする 2: 利用者が付けたラベルで感情ごとに分ける 3: 最小件数に満たない感情は理由つきで作らない]
//   補足: [ラベル無しの区間を平常と見なさない(件数だけ数える)。推定ラベルは既定で使わない。最小件数は実測で決める未決事項のため呼び出し側が指定する。設計: docs/design/v1-training/emotion-expression.md]
// }
public sealed class EmotionStylePlanner
{
    private readonly UsageRoutingService _routing = new();
    private readonly TrainingEligibilityService _eligibility = new();

    // {
    //   責務: [Plan: 感情別スタイルの作成計画を作る]
    //   処理: [話者の区間を絞る→発話の教師か確認→ラベルで感情ごとに分ける→最小件数で作る・作らないを決める]
    //   引数: [segments: データセットの区間, classification: 会話・歌唱の分類, speakerId: 対象話者, minSegmentsPerEmotion: 感情1つに必要な最小区間数(1以上), includeInferred: 推定ラベルも使うか, userExcludedSegmentIds: 利用者が除外した区間, isAudioReadable: 音声が読めるかの判定]
    //   戻り値: [作れる感情(名前順)・作れない感情と理由・ラベル無しの件数]
    //   エラー: [話者IDが空、最小件数が1未満のとき例外]
    // }
    public EmotionStylePlan Plan(
        IEnumerable<UniversalVoiceSegment> segments,
        ContentClassificationRecord classification,
        string speakerId,
        int minSegmentsPerEmotion,
        bool includeInferred = false,
        IReadOnlySet<string>? userExcludedSegmentIds = null,
        Func<UniversalVoiceSegment, bool>? isAudioReadable = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(classification);
        if (string.IsNullOrWhiteSpace(speakerId)) throw new ArgumentException("話者IDが空です。", nameof(speakerId));
        if (minSegmentsPerEmotion < 1) throw new ArgumentOutOfRangeException(nameof(minSegmentsPerEmotion), "最小件数は1以上にしてください。");

        var mine = segments.Where(x => x.SpeakerId == speakerId).OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToArray();
        var excluded = userExcludedSegmentIds ?? new HashSet<string>();
        var routing = _routing.Route(mine, classification, excluded).Decisions.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);
        var eligibility = _eligibility.Evaluate(mine, new TrainingEligibilityInput
        {
            SpeakerId = speakerId,
            Target = "talk",
            UserExcludedSegmentIds = excluded,
            IsAudioReadable = isAudioReadable
        }).Decisions.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);

        var groups = new Dictionary<string, List<UniversalVoiceSegment>>(StringComparer.Ordinal);
        var unlabeled = 0;
        foreach (var segment in mine)
        {
            // 発話の教師になる区間だけが対象。歌唱・曖昧・除外はここでは数えない。
            if (routing[segment.SegmentId].Talk.Role != UsageRole.Primary) continue;
            if (eligibility[segment.SegmentId].Eligibility == "excluded") continue;
            var label = segment.Emotion;
            if (label is null || label.Validate() is not null || (label.Source == EmotionLabelRecord.SourceInferred && !includeInferred))
            {
                unlabeled++;
                continue;
            }
            if (!groups.TryGetValue(label.Name, out var list)) groups[label.Name] = list = [];
            list.Add(segment);
        }

        var styles = new List<EmotionStyleGroup>();
        var skipped = new List<EmotionStyleSkip>();
        foreach (var (name, list) in groups.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (list.Count < minSegmentsPerEmotion)
                skipped.Add(new(name, list.Count, $"感情「{name}」の区間が{list.Count}件で、必要な{minSegmentsPerEmotion}件に足りません。"));
            else
                styles.Add(new(name, [.. list.Select(x => x.SegmentId)], [.. list.Select(x => x.AudioPath)]));
        }
        return new(styles, skipped, unlabeled);
    }
}
