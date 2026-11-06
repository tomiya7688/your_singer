using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

// 1区間とその有声F0(Hz)。F0の読込は呼び出し側が行う(このサービスは入出力を持たない)。
public sealed class UtilityObservation
{
    public required UniversalVoiceSegment Segment { get; init; }
    public IReadOnlyList<double> F0Hz { get; init; } = [];
}

// 話者×用途ごとの区間Utility(0〜1)を決定的に計算する。重み(training_weight)は計算しない。
// 正本: docs/design/v1-training/contracts-and-weighting.md「U算出」。
public sealed class TrainingUtilityService
{
        private const double BaseHz = 55.0;
    private const double BinSemitones = 6.0;
    private const string PlaceholderAlignment = "legacy-uniform-placeholder";

    private static readonly HashSet<string> KnownPhonemes = new(
        JapaneseCoverageCatalog.CorePhonemes
            .Concat(JapaneseCoverageCatalog.Palatalized)
            .Concat(JapaneseCoverageCatalog.Foreign),
        StringComparer.Ordinal);

    // {
    //   責務: [Compute: eligible集合の観測から、区間ごとのUtilityをSegmentId昇順で返す]
    //   処理: [
    //     1: 確実に観測した音素と有効F0だけで、音素・音域binの出現数を数える
    //     2: 区間内の最大希少度を求め、歌唱は音素と音域の平均にする
    //     3: 観測根拠がなければ value=null と unavailable を返す(評価上の中立0.5は重み計算側で使う)
    //   ]
    //   戻り値: [utilities: SegmentId→Utility。呼び出し側は同一話者・同一用途のeligibleだけを渡す]
    // }
    public IReadOnlyDictionary<string, TrainingUtilityAssessment> Compute(
        IEnumerable<UtilityObservation> eligible,
        string target)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        if (!TrainingAssessmentVocabulary.Targets.Contains(target))
            throw new ArgumentException($"未対応の用途です: {target}", nameof(target));
        var singing = target == "singing";
        var ordered = eligible.OrderBy(x => x.Segment.SegmentId, StringComparer.Ordinal).ToArray();

        var phonemeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var binCounts = new Dictionary<int, int>();
        var perSegment = ordered
            .Select(x => (
                Id: x.Segment.SegmentId,
                Phonemes: ObservedPhonemes(x.Segment, singing),
                Bins: singing ? ObservedBins(x) : []))
            .ToArray();
        foreach (var item in perSegment)
        {
            foreach (var p in item.Phonemes)
                phonemeCounts[p] = phonemeCounts.GetValueOrDefault(p) + 1;
            foreach (var b in item.Bins)
                binCounts[b] = binCounts.GetValueOrDefault(b) + 1;
        }

        var result = new Dictionary<string, TrainingUtilityAssessment>(StringComparer.Ordinal);
        foreach (var item in perSegment)
        {
            double? phonemeScore = item.Phonemes.Count == 0 ? null : item.Phonemes.Max(p => Rarity(phonemeCounts[p]));
            double? rangeScore = item.Bins.Count == 0 ? null : item.Bins.Max(b => Rarity(binCounts[b]));
            var scores = new[] { phonemeScore, rangeScore }.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            var rare = item.Phonemes.Any(p => phonemeCounts[p] == 1);
            result[item.Id] = scores.Length == 0
                ? new TrainingUtilityAssessment
                {
                    Value = null, Status = ConfidenceStatus.Unavailable, ReasonCodes = []
                }
                : new TrainingUtilityAssessment
                {
                    Value = scores.Average(), Status = ConfidenceStatus.Measured,
                    ReasonCodes = rare ? ["rare_phoneme"] : []
                };
        }
        return result;
    }

    private static double Rarity(int count) => 1.0 / Math.Sqrt(Math.Max(1, count));

    // 確実に観測した音素(同一区間の重複は1回)。推定(placeholder)・未知語彙・不正時刻は数えない。
    private static List<string> ObservedPhonemes(UniversalVoiceSegment segment, bool singing)
    {
        if (string.IsNullOrEmpty(segment.AlignmentMethod) || segment.AlignmentMethod == PlaceholderAlignment)
            return [];
        if (segment.AlignmentConfidenceStatus is ConfidenceStatus.Unavailable or ConfidenceStatus.Invalid)
            return [];
        if (singing && !segment.HasValidSingingTrainingAlignment()) return [];
        return segment.Phonemes
            .Where(x => x is not null && KnownPhonemes.Contains(x.Phoneme ?? string.Empty) &&
                        double.IsFinite(x.StartSec) && double.IsFinite(x.EndSec) &&
                        x.StartSec >= 0 && x.EndSec > x.StartSec)
            .Select(x => x.Phoneme)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // 音高の信頼性が正で、F0が有限の正値のときだけ6半音幅のbinにする。binは区間ごとに1回だけ数え、n(bin)を音素と同じ「区間数」にする(フレーム数の多い区間が有利にならない)。
    private static List<int> ObservedBins(UtilityObservation obs)
    {
        var reliability = obs.Segment.PitchReliability;
        if (!double.IsFinite(reliability) || reliability <= 0) return [];
        return obs.F0Hz
            .Where(f => double.IsFinite(f) && f > 0)
            .Select(f => (int)Math.Floor(12.0 * Math.Log2(f / BaseHz) / BinSemitones))
            .Distinct()
            .ToList();
    }
}
