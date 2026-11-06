using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// 棚卸しの入力。ユーザー除外などの事実と、秒数・F0・音声可読性の取得元を呼び出し側が渡す。
public sealed class SpeakerDataInventoryInput
{
    public required string SpeakerId { get; init; }
    public required string DatasetFingerprint { get; init; }
    public string? ManualOverrideRevision { get; init; }
    public required ContentClassificationRecord Classification { get; init; }
    public IReadOnlySet<string> UserExcludedSegmentIds { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> ConfirmedWrongSpeakerSegmentIds { get; init; } = new HashSet<string>();
    // 区間の再生秒数。測れない場合は null(0秒と読み替えない)。
    public required Func<UniversalVoiceSegment, double?> DurationSeconds { get; init; }
    public Func<UniversalVoiceSegment, bool>? IsAudioReadable { get; init; }
    public Func<UniversalVoiceSegment, IReadOnlyList<double>>? F0Provider { get; init; }
}

// 話者ごとに、発話教師・歌唱教師・声質参照の実効量を別軸で集計する。学習方式やBackendは決めない。
// 正本: docs/design/v1-training/learning-system-design.md「4. Basic Source Profile」。
public sealed class SpeakerDataInventoryService
{
    public const string CalculationVersion = "speaker-data-inventory-v1";
    private readonly UsageRoutingService _routing = new();
    private readonly TrainingEligibilityService _eligibility = new();

    // {
    //   責務: [Build: 話者の区間から、用途別の実効データ量と測定状況を決定的に集計する]
    //   処理: [
    //     1: routing と適格性(#86/#81)で、発話教師・歌唱教師・声質参照の集合を決める
    //     2: 秒数・独立数・カバレッジを別々に集計する(同じ原音は CacheKey で重複を数えない)
    //     3: 測れなかった指標は measurement_status に残し、0として確定しない
    //   ]
    //   戻り値: [inventory: 閾値を持たない棚卸し。InventoryRevision で素材・修正・規則版の変化を検出できる]
    // }
    public SpeakerDataInventory Build(IEnumerable<UniversalVoiceSegment> segments, SpeakerDataInventoryInput input)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SpeakerId)) throw new ArgumentException("話者IDが空です。", nameof(input));
        if (string.IsNullOrWhiteSpace(input.DatasetFingerprint)) throw new ArgumentException("素材のfingerprintが空です。", nameof(input));
        var all = segments.Where(x => x.SpeakerId == input.SpeakerId).OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToArray();
        var routing = _routing.Route(all, input.Classification, input.UserExcludedSegmentIds).Decisions
            .ToDictionary(x => x.SegmentId, StringComparer.Ordinal);
        var talk = Eligible(all, input, "talk");
        var singing = Eligible(all, input, "singing");

        var speechRaw = all.Where(x => routing[x.SegmentId].Talk.Role == UsageRole.Primary).ToArray();
        var speechEligible = speechRaw.Where(x => talk.Contains(x.SegmentId)).ToArray();
        var singingRaw = all.Where(x => routing[x.SegmentId].Singing.Role == UsageRole.Primary).ToArray();
        var singingEligible = singingRaw.Where(x => singing.Contains(x.SegmentId)).ToArray();
        var identity = all.Where(x => IsIdentityReference(x, routing[x.SegmentId], input)).ToArray();

        var f0 = singingEligible.ToDictionary(x => x.SegmentId, x => input.F0Provider?.Invoke(x) ?? [], StringComparer.Ordinal);
        var bins = singingEligible.SelectMany(x => TrainingUtilityService.ObservedBins(x, f0[x.SegmentId])).Distinct().Count();
        var speechPhonemes = speechEligible.SelectMany(x => TrainingUtilityService.ObservedPhonemes(x, singing: false))
            .Distinct(StringComparer.Ordinal).Count();

        var durationStatus = Status(all.Select(x => input.DurationSeconds(x) is not null));
        var inventory = new SpeakerDataInventory
        {
            SchemaVersion = SpeakerDataInventory.CurrentSchemaVersion, SpeakerId = input.SpeakerId,
            DatasetFingerprint = input.DatasetFingerprint, CalculationVersion = CalculationVersion,
            InventoryRevision = string.Empty,
            Speech = new SpeechInventory
            {
                RawSeconds = Seconds(speechRaw, input), EligibleSeconds = Seconds(speechEligible, input),
                DistinctRecordings = speechEligible.Select(x => x.SourceId).Distinct(StringComparer.Ordinal).Count(),
                IndependentUtterances = speechEligible.Select(x => x.CacheKey).Distinct(StringComparer.Ordinal).Count(),
                TranscriptUsableCount = speechEligible.Count(x => !string.IsNullOrWhiteSpace(x.Transcript)),
                PhonemeCoverageCount = speechPhonemes
            },
            Singing = new SingingInventory
            {
                RawSeconds = Seconds(singingRaw, input), EligibleSeconds = Seconds(singingEligible, input),
                DistinctRecordings = singingEligible.Select(x => x.SourceId).Distinct(StringComparer.Ordinal).Count(),
                IndependentPhrases = singingEligible.Select(x => x.CacheKey).Distinct(StringComparer.Ordinal).Count(),
                PitchBinsObserved = bins
            },
            IdentityReference = new IdentityReferenceInventory
            {
                EligibleSeconds = Seconds(identity, input),
                DistinctSources = identity.Select(x => x.SourceId).Distinct(StringComparer.Ordinal).Count()
            },
            MeasurementStatus = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["duration"] = durationStatus,
                ["transcription"] = Status(speechEligible.Select(x => x.IsAsrConfidenceMeasured())),
                ["singing_f0"] = Status(singingEligible.Select(x => f0[x.SegmentId].Count > 0)),
                ["speaker_identity"] = Status(identity.Select(x => x.SpeakerEmbedding.Count > 0))
            },
            DerivedTags = Tags(speechEligible.Length, singingEligible.Length, identity.Length)
        };
        return Rebuild(inventory, input.ManualOverrideRevision);
    }

    private HashSet<string> Eligible(UniversalVoiceSegment[] all, SpeakerDataInventoryInput input, string target) =>
        _eligibility.Evaluate(all, new TrainingEligibilityInput
        {
            SpeakerId = input.SpeakerId, Target = target, UserExcludedSegmentIds = input.UserExcludedSegmentIds,
            ConfirmedWrongSpeakerSegmentIds = input.ConfirmedWrongSpeakerSegmentIds, IsAudioReadable = input.IsAudioReadable
        }).Decisions.Where(x => x.Eligibility == "eligible").Select(x => x.SegmentId).ToHashSet(StringComparer.Ordinal);

    // 品質に難があっても声質だけ使える区間を残す。除外(ユーザー除外・誤話者・読めない音声・有声なし)と確認待ちは含めない。
    private bool IsIdentityReference(UniversalVoiceSegment segment, UsageRoutingDecision decision, SpeakerDataInventoryInput input)
    {
        if (decision.Talk.Role is not (UsageRole.Primary or UsageRole.FeatureOnly) &&
            decision.Singing.Role is not (UsageRole.Primary or UsageRole.FeatureOnly)) return false;
        var excluded = _eligibility.Evaluate([segment], new TrainingEligibilityInput
        {
            SpeakerId = input.SpeakerId, Target = "talk", UserExcludedSegmentIds = input.UserExcludedSegmentIds,
            ConfirmedWrongSpeakerSegmentIds = input.ConfirmedWrongSpeakerSegmentIds, IsAudioReadable = input.IsAudioReadable
        }).Decisions.Single();
        return excluded.Eligibility != "excluded";
    }

    private static double Seconds(IEnumerable<UniversalVoiceSegment> segments, SpeakerDataInventoryInput input) =>
        segments.Sum(x => input.DurationSeconds(x) is { } d && double.IsFinite(d) && d > 0 ? d : 0.0);

    // 全件が測定できていれば measured、一部なら partially_measured、1件も無い/全件測れなければ unavailable。
    private static string Status(IEnumerable<bool> measured)
    {
        var list = measured.ToArray();
        if (list.Length == 0 || list.All(x => !x)) return ConfidenceStatus.Unavailable;
        return list.All(x => x) ? ConfidenceStatus.Measured : "partially_measured";
    }

    private static List<string> Tags(int speech, int singing, int identity)
    {
        var tags = new List<string>();
        tags.Add(speech > 0 ? "has_speech_teacher" : "speech_teacher_absent");
        tags.Add(singing > 0 ? "has_singing_teacher" : "singing_teacher_absent");
        if (identity > 0) tags.Add("has_identity_reference");
        return tags;
    }

    private static SpeakerDataInventory Rebuild(SpeakerDataInventory x, string? overrideRevision)
    {
        var inv = CultureInfo.InvariantCulture;
        var canonical = string.Join('\n', CalculationVersion, x.SpeakerId, x.DatasetFingerprint, overrideRevision ?? "none",
            $"speech={x.Speech.RawSeconds.ToString("R", inv)},{x.Speech.EligibleSeconds.ToString("R", inv)},{x.Speech.DistinctRecordings},{x.Speech.IndependentUtterances},{x.Speech.TranscriptUsableCount},{x.Speech.PhonemeCoverageCount}",
            $"singing={x.Singing.RawSeconds.ToString("R", inv)},{x.Singing.EligibleSeconds.ToString("R", inv)},{x.Singing.DistinctRecordings},{x.Singing.IndependentPhrases},{x.Singing.PitchBinsObserved}",
            $"identity={x.IdentityReference.EligibleSeconds.ToString("R", inv)},{x.IdentityReference.DistinctSources}",
            string.Join(',', x.MeasurementStatus.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}:{k.Value}")));
        return new SpeakerDataInventory
        {
            SchemaVersion = x.SchemaVersion, SpeakerId = x.SpeakerId, DatasetFingerprint = x.DatasetFingerprint,
            CalculationVersion = x.CalculationVersion, Speech = x.Speech, Singing = x.Singing,
            IdentityReference = x.IdentityReference, MeasurementStatus = x.MeasurementStatus, DerivedTags = x.DerivedTags,
            InventoryRevision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
        };
    }
}
