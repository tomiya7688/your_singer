using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// 重み計算の入力。品質の各指標は計測状態付きで渡し、未計測を0に変換しない。
public sealed class TrainingWeightInput
{
    public required string SegmentId { get; init; }
    public required string Eligibility { get; init; }
    public IReadOnlyDictionary<string, TrainingMeasurement> QualityComponents { get; init; } =
        new Dictionary<string, TrainingMeasurement>();
    public required TrainingUtilityAssessment Utility { get; init; }
}

public sealed class TrainingWeightResult
{
    public required TrainingQualityAssessment Quality { get; init; }
    public required double RawWeight { get; init; }
    public required double TrainingWeight { get; init; }
}

// v1の決定的な重み計算(初期policy)。実証済みの最適式ではなく、回帰テストの基準。
// 正本: docs/design/v1-training/contracts-and-weighting.md「3. v1の決定的な重み計算」。
public sealed class TrainingWeightPolicy
{
    public const string PolicyVersion = "training-weight-v1";
    public const double NeutralValue = 0.5;
    public const double MinWeight = 0.25;
    public const double MaxWeight = 2.0;

    // {
    //   責務: [Compute: eligible集合の区間ごとに raw_weight と training_weight を決定的に計算する]
    //   処理: [
    //     1: 入力を検証する(重複ID・範囲外・非有限は拒否、eligibleが0件ならエラー)
    //     2: Qは measured の指標の平均、未計測のQ/Uは評価上0.5を使い状態は残す
    //     3: raw=(0.35+0.65Q)*(1+0.75U)、weight=clamp(raw/mean_raw,0.25,2)。非eligibleは0
    //   ]
    //   戻り値: [results: SegmentId→結果。入力順に依存しない]
    // }
    public IReadOnlyDictionary<string, TrainingWeightResult> Compute(IEnumerable<TrainingWeightInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var ordered = inputs.OrderBy(x => x?.SegmentId, StringComparer.Ordinal).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ordered)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.SegmentId))
                throw new ArgumentException("区間IDが空です。", nameof(inputs));
            if (!seen.Add(item.SegmentId))
                throw new ArgumentException($"区間IDが重複しています: {item.SegmentId}", nameof(inputs));
            if (!TrainingAssessmentVocabulary.Eligibilities.Contains(item.Eligibility))
                throw new ArgumentException($"{item.SegmentId}: 未対応のeligibilityです: {item.Eligibility}", nameof(inputs));
        }

        var prepared = ordered.Select(x =>
        {
            var quality = BuildQuality(x);
            var u = EffectiveUtility(x);
            var q = quality.Value ?? NeutralValue;
            var raw = (0.35 + 0.65 * q) * (1.0 + 0.75 * u);
            return (Input: x, Quality: quality, Raw: raw);
        }).ToArray();

        var eligible = prepared.Where(x => x.Input.Eligibility == "eligible").ToArray();
        if (eligible.Length == 0)
            throw new InvalidOperationException("eligibleな区間がないため重みを計算できません。");
        var meanRaw = eligible.Sum(x => x.Raw) / eligible.Length;

        var results = new Dictionary<string, TrainingWeightResult>(StringComparer.Ordinal);
        foreach (var item in prepared)
        {
            var isEligible = item.Input.Eligibility == "eligible";
            var weight = isEligible ? Math.Clamp(item.Raw / meanRaw, MinWeight, MaxWeight) : 0.0;
            results[item.Input.SegmentId] = new TrainingWeightResult
            {
                Quality = item.Quality,
                RawWeight = isEligible ? item.Raw : 0.0,
                TrainingWeight = weight
            };
        }
        return results;
    }

    // measured の指標だけの算術平均。1つも無ければ value=null/status=unavailable(0に変換しない)。
    private static TrainingQualityAssessment BuildQuality(TrainingWeightInput input)
    {
        var components = new Dictionary<string, TrainingMeasurement>(StringComparer.Ordinal);
        var measured = new List<double>();
        foreach (var (name, m) in input.QualityComponents.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (m is null) throw new ArgumentException($"{input.SegmentId}: 品質指標 {name} が null です。");
            RequireRange(m.Value, $"{input.SegmentId}: 品質指標 {name}");
            if (m.Status == ConfidenceStatus.Measured)
            {
                if (m.Value is null) throw new ArgumentException($"{input.SegmentId}: 品質指標 {name} は measured ですが値がありません。");
                measured.Add(m.Value.Value);
            }
            components[name] = m;
        }
        return measured.Count == 0
            ? new TrainingQualityAssessment { Value = null, Status = ConfidenceStatus.Unavailable, Components = components }
            : new TrainingQualityAssessment { Value = measured.Average(), Status = ConfidenceStatus.Measured, Components = components };
    }

    private static double EffectiveUtility(TrainingWeightInput input)
    {
        var utility = input.Utility ?? throw new ArgumentException($"{input.SegmentId}: utility がありません。");
        RequireRange(utility.Value, $"{input.SegmentId}: utility");
        if (utility.Status == ConfidenceStatus.Measured)
            return utility.Value ?? throw new ArgumentException($"{input.SegmentId}: utility は measured ですが値がありません。");
        return NeutralValue;
    }

    private static void RequireRange(double? value, string label)
    {
        if (value is { } v && (!double.IsFinite(v) || v < 0 || v > 1))
            throw new ArgumentException($"{label} は0〜1の有限数が必要です(値: {v})。");
    }
}
