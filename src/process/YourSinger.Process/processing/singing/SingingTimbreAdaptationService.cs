using System.Security.Cryptography;
using System.Text;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

// 補助条件の出所(provenance)。Weight は暫定の一律1.0で、品質による重み付けはまだ行わない。
public sealed record SingingTimbreReference(string SegmentId, string SourceId, string CacheKey, string AudioPath, double Weight);

public sealed record SingingTimbreSkip(string SegmentId, string Reason);

// Adapter が実際に特徴を消費したことの記録(segment_id・用途・方式・input hash)。
public sealed record SingingTimbreConsumption(IReadOnlyList<string> SegmentIds, string Purpose, string Method, string InputHash);

// 歌唱モデルへ渡す、発話由来の声質参照(経路S1)。発話は歌唱の音符・歌詞教師ではなく、補助条件(feature_only)にだけ使う。
public sealed class SingingTimbreAdaptationInput
{
    public const string Purpose = "speaker_timbre_condition";
    public const string Method = "shared_speaker_encoder";

    public required string SpeakerId { get; init; }
    // 同一話者の発話区間(SegmentId昇順)。歌唱教師が0件でも作れる。
    public required IReadOnlyList<SingingTimbreReference> References { get; init; }
    public required IReadOnlyList<SingingTimbreSkip> Skipped { get; init; }
    // 参照の集合から決まるhash。集合・音声の識別が変わると変わり、Adapter の消費記録と照合する。
    public required string InputHash { get; init; }
    // ジョブに残す利用記録。Adapter の消費が確認できるまで Used=false。
    public required SharedFeatureUsageEntry Usage { get; init; }
    public bool HasReferences => References.Count > 0;
}

// {
//   責務: [SingingTimbreAdaptationService: 同一話者の発話区間から、歌唱モデルへ渡す声質参照を選び、消費の確認までを記録する]
//   補足: [
//     正本: docs/design/v1-training/routing-and-cross-domain.md「実現経路（設計）」S1。
//     DiffSinger の固定版は連続の話者ベクトルを受け取れないため、話者エンコーダの限定パッチが入るまで Support=unsupported として記録し、
//     「トーク/歌唱の相互利用」とは称さない(決定記録 D-09)。実音声での効果は未検証。
//   ]
// }
public sealed class SingingTimbreAdaptationService
{
    public const string Feature = "speaker_identity_from_speech_audio";
    private readonly UsageRoutingService _routing = new();
    private readonly TrainingEligibilityService _eligibility = new();

    // {
    //   責務: [Build: 同一話者の発話区間から、歌唱モデルの声質条件にする参照を選ぶ]
    //   処理: [
    //     1: 無効なら参照を選ばず、理由付きの利用記録にする
    //     2: routing で発話の primary かつ対象話者の区間を選び、除外(ユーザー除外・読めない音声・有声なし)を除く
    //     3: 参照が無ければ Used=false と理由、あれば Backend の対応状況を記録する(消費の確認までは Used=false)
    //   ]
    //   引数: [backendSupportsSpeakerEncoder: 対象Backendが話者エンコーダ条件を受け取れるか(固定版のDiffSingerは false)]
    //   戻り値: [input: 参照・除いた区間と理由・input hash・利用記録]
    // }
    public SingingTimbreAdaptationInput Build(
        IEnumerable<UniversalVoiceSegment> segments,
        ContentClassificationRecord classification,
        string speakerId,
        bool enabled = true,
        bool backendSupportsSpeakerEncoder = false,
        IReadOnlySet<string>? userExcludedSegmentIds = null,
        Func<UniversalVoiceSegment, bool>? isAudioReadable = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(classification);
        if (string.IsNullOrWhiteSpace(speakerId)) throw new ArgumentException("話者IDが空です。", nameof(speakerId));
        if (!enabled) return Result(speakerId, [], [], backendSupportsSpeakerEncoder, "発話由来の声質参照が無効に設定されています。");

        var all = segments.Where(x => x.SpeakerId == speakerId).OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToArray();
        var excluded = userExcludedSegmentIds ?? new HashSet<string>();
        var routing = _routing.Route(all, classification, excluded).Decisions.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);
        var eligibility = _eligibility.Evaluate(all, new TrainingEligibilityInput
        {
            SpeakerId = speakerId, Target = "singing", UserExcludedSegmentIds = excluded, IsAudioReadable = isAudioReadable
        }).Decisions.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);

        var references = new List<SingingTimbreReference>();
        var skipped = new List<SingingTimbreSkip>();
        foreach (var segment in all)
        {
            var decision = routing[segment.SegmentId];
            if (decision.Talk.Role != UsageRole.Primary)
            {
                // 発話でない区間は参照にしない(歌唱は教師、曖昧・除外は混ぜない)。
                if (decision.Singing.Role != UsageRole.Primary)
                    skipped.Add(new(segment.SegmentId, decision.Talk.ReasonCodes.FirstOrDefault() ?? decision.Talk.Role));
                continue;
            }
            var check = eligibility[segment.SegmentId];
            if (check.Eligibility == "excluded")
                skipped.Add(new(segment.SegmentId, check.PrimaryReason ?? "excluded"));
            else
                references.Add(new(segment.SegmentId, segment.SourceId, segment.CacheKey, segment.AudioPath, 1.0));
        }
        return Result(speakerId, references, skipped, backendSupportsSpeakerEncoder,
            references.Count == 0 ? "この話者に使える発話区間がありません。" : null);
    }

    // {
    //   責務: [Confirm: Adapter の消費記録が入力と一致するときだけ、利用記録を Used=true にする]
    //   エラー: [別話者・用途/方式・input hash・segment_id の不一致で InvalidOperationException(効果を偽って記録しない)]
    // }
    public static SharedFeatureUsageEntry Confirm(SingingTimbreAdaptationInput input, SingingTimbreConsumption consumption)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(consumption);
        if (!input.HasReferences || input.Usage.Support != SharedFeatureSupport.Supported)
            throw new InvalidOperationException("この入力は Backend が対応していない、または参照がないため、消費を確認できません。");
        if (consumption.Purpose != SingingTimbreAdaptationInput.Purpose || consumption.Method != SingingTimbreAdaptationInput.Method)
            throw new InvalidOperationException("Adapterの消費記録の用途または方式が一致しません。");
        if (consumption.InputHash != input.InputHash)
            throw new InvalidOperationException("Adapterの消費記録のinput hashが入力と一致しません(入力が変わった可能性があります)。");
        var expected = input.References.Select(x => x.SegmentId).Order(StringComparer.Ordinal);
        if (!expected.SequenceEqual(consumption.SegmentIds.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Adapterが消費した区間が、選んだ参照と一致しません。");
        return new SharedFeatureUsageEntry
        {
            Feature = Feature, Domain = SharedFeatureDomain.Speech, Support = SharedFeatureSupport.Supported, Used = true,
            Reason = $"発話区間 {consumption.SegmentIds.Count} 件から作った声質を、歌唱モデルの条件として使いました。実音声での効果は未検証です。"
        };
    }

    private static SingingTimbreAdaptationInput Result(
        string speakerId, List<SingingTimbreReference> references, List<SingingTimbreSkip> skipped,
        bool backendSupports, string? unusedReason) => new()
    {
        SpeakerId = speakerId, References = references, Skipped = skipped, InputHash = Hash(speakerId, references),
        Usage = new SharedFeatureUsageEntry
        {
            Feature = Feature, Domain = SharedFeatureDomain.Speech, Used = false,
            Support = backendSupports ? SharedFeatureSupport.Supported : SharedFeatureSupport.Unsupported,
            Reason = unusedReason ?? (backendSupports
                ? $"発話区間 {references.Count} 件の声質を歌唱モデルの条件にします(Adapterの消費が確認できるまで『使用済み』にはしません)。"
                : "この版の歌唱学習(DiffSinger固定版)は、連続の話者ベクトル条件を受け取れません。話者エンコーダの限定パッチが入るまで『未対応』です。")
        }
    };

    private static string Hash(string speakerId, List<SingingTimbreReference> references)
    {
        var canonical = string.Join('\n', speakerId, SingingTimbreAdaptationInput.Purpose, SingingTimbreAdaptationInput.Method,
            string.Join(';', references.OrderBy(x => x.SegmentId, StringComparer.Ordinal).Select(x => $"{x.SegmentId}|{x.SourceId}|{x.CacheKey}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
