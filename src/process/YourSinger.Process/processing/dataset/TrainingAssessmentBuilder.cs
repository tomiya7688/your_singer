using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// 評価の組み立てに必要な入力。話者・用途ごとに1回呼ぶ。
public sealed class TrainingAssessmentBuildInput
{
    public required string JobId { get; init; }
    public required string SpeakerId { get; init; }
    public required string Target { get; init; }
    public required string SourceDatasetFingerprint { get; init; }
    public string? ManualOverrideRevision { get; init; }
    public IReadOnlySet<string> UserExcludedSegmentIds { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> ConfirmedWrongSpeakerSegmentIds { get; init; } = new HashSet<string>();
    public Func<UniversalVoiceSegment, bool>? IsAudioReadable { get; init; }
    // 歌唱の有声F0(Hz)。未指定なら音域は測れないものとして扱う。
    public Func<UniversalVoiceSegment, IReadOnlyList<double>>? F0Provider { get; init; }
}

// 適格性(#81) → Utility(#82) → 重み(#83) をつなぎ、保存できる TrainingAssessmentRecord を作る。
public sealed class TrainingAssessmentBuilder
{
    private const string PlaceholderAlignment = "legacy-uniform-placeholder";
    private readonly TrainingEligibilityService _eligibility = new();
    private readonly TrainingUtilityService _utility = new();
    private readonly TrainingWeightPolicy _weights = new();

    // {
    //   責務: [Build: 区間の適格性・品質・Utility・重みを決定的に組み立てる]
    //   処理: [
    //     1: 適格性を判定し、eligibleだけからUtilityと重みを計算する
    //     2: 品質は区間の計測状態付き指標から作り、未計測を0に変換しない
    //     3: SegmentId昇順のrecordを返す
    //   ]
    //   エラー: [eligibleが0件のとき InvalidOperationException(学習計画不可)]
    // }
    public TrainingAssessmentRecord Build(IEnumerable<UniversalVoiceSegment> segments, TrainingAssessmentBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(input);
        var all = segments.ToArray();
        var byId = all.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);
        var decisions = _eligibility.Evaluate(all, new TrainingEligibilityInput
        {
            SpeakerId = input.SpeakerId, Target = input.Target,
            UserExcludedSegmentIds = input.UserExcludedSegmentIds,
            ConfirmedWrongSpeakerSegmentIds = input.ConfirmedWrongSpeakerSegmentIds,
            IsAudioReadable = input.IsAudioReadable
        }).Decisions;

        var utilities = _utility.Compute(
            decisions.Where(x => x.Eligibility == "eligible").Select(x => new UtilityObservation
            {
                Segment = byId[x.SegmentId],
                F0Hz = input.Target == "singing" ? input.F0Provider?.Invoke(byId[x.SegmentId]) ?? [] : []
            }), input.Target);
        var unavailableUtility = () => new TrainingUtilityAssessment
        {
            Value = null, Status = ConfidenceStatus.Unavailable, ReasonCodes = []
        };

        var weightInputs = decisions.Select(x => new TrainingWeightInput
        {
            SegmentId = x.SegmentId, Eligibility = x.Eligibility,
            QualityComponents = QualityComponents(byId[x.SegmentId], input.Target),
            Utility = utilities.GetValueOrDefault(x.SegmentId) ?? unavailableUtility()
        }).ToArray();
        var weights = _weights.Compute(weightInputs);

        return new TrainingAssessmentRecord
        {
            SchemaVersion = TrainingAssessmentRecord.CurrentSchemaVersion,
            PolicyVersion = TrainingWeightPolicy.PolicyVersion, JobId = input.JobId, SpeakerId = input.SpeakerId,
            Target = input.Target, SourceDatasetFingerprint = input.SourceDatasetFingerprint,
            ManualOverrideRevision = input.ManualOverrideRevision,
            Items = weightInputs.OrderBy(x => x.SegmentId, StringComparer.Ordinal).Select(x =>
            {
                var decision = decisions.First(d => d.SegmentId == x.SegmentId);
                return new TrainingAssessmentItem
                {
                    SegmentId = x.SegmentId, Eligibility = x.Eligibility, ReasonCodes = [.. decision.ReasonCodes],
                    Quality = weights[x.SegmentId].Quality, Utility = x.Utility,
                    RawWeight = weights[x.SegmentId].RawWeight, TrainingWeight = weights[x.SegmentId].TrainingWeight
                };
            }).ToList()
        };
    }

    // 区間ごとの品質指標。実測のASR・alignment信頼度だけを measured にし、それ以外は元の状態を保つ。
    // 推定(placeholder)のalignmentは信頼度を使わない。歌唱のF0信頼性は較正状態が無いので値を使わない。
    private static Dictionary<string, TrainingMeasurement> QualityComponents(UniversalVoiceSegment s, string target)
    {
        var result = new Dictionary<string, TrainingMeasurement>(StringComparer.Ordinal)
        {
            ["asr_confidence"] = Measurement(s.IsAsrConfidenceMeasured(), s.AsrConfidence, s.AsrConfidenceStatus),
            ["alignment_confidence"] = s.AlignmentMethod == PlaceholderAlignment
                ? new TrainingMeasurement { Value = null, Status = ConfidenceStatus.Unavailable }
                : Measurement(s.IsAlignmentConfidenceMeasured(), s.AlignmentConfidence, s.AlignmentConfidenceStatus)
        };
        if (target == "singing")
            result["f0_reliability"] = new TrainingMeasurement { Value = null, Status = ConfidenceStatus.LegacyUnknown };
        return result;
    }

    private static TrainingMeasurement Measurement(bool measured, double value, string status)
    {
        if (!measured) return new TrainingMeasurement { Value = null, Status = status };
        return double.IsFinite(value) && value is >= 0 and <= 1
            ? new TrainingMeasurement { Value = value, Status = ConfidenceStatus.Measured }
            : new TrainingMeasurement { Value = null, Status = ConfidenceStatus.Invalid };
    }
}
