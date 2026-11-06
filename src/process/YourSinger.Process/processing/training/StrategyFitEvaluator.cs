using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

public static class StrategyOutcome
{
    public const string Met = "met";
    public const string Unmet = "unmet";
    // 測れていない値は条件の真偽を決められない。0 と同一視しない。
    public const string Unknown = "unknown";
}

public sealed record RequirementResult(StrategyRequirement Requirement, string Outcome, double? Actual, string Reason);

public sealed class StrategyFit
{
    public required string Outcome { get; init; }
    public required IReadOnlyList<RequirementResult> Results { get; init; }
    // 候補にならない理由(対象・プリセット・能力・条件不成立)。候補なら空。
    public required IReadOnlyList<string> UnmetReasons { get; init; }
}

public sealed record StrategyCandidate(TrainingStrategyEntry Entry, StrategyFit Fit);

// 戦略の成立条件(requirements)を、話者の棚卸しと照合する。固定の閾値は持たず、条件は戦略が宣言する。
// 目標の target とプリセットは棚卸しから変更しない。
public static class StrategyFitEvaluator
{
    // {
    //   責務: [Evaluate: 戦略が対象・プリセット・能力・素材量の条件を満たすか判定する]
    //   処理: [
    //     1: 対象 target・プリセットの対応と、必要な能力を確認する
    //     2: 各 requirement を role × metric の実測値と比べる(測れない値は unknown)
    //     3: 1つでも unmet なら unmet、unmet が無く unknown があれば unknown、全て met なら met
    //   ]
    // }
    public static StrategyFit Evaluate(
        TrainingStrategyEntry entry, SpeakerDataInventory inventory, string target, string? presetId)
    {
        var reasons = new List<string>();
        if (!entry.Strategy.SupportedTargets.Contains(target)) reasons.Add($"この戦略は {target} に対応していません。");
        if (presetId is not null && entry.Strategy.SupportedPresetIds.Count > 0 && !entry.Strategy.SupportedPresetIds.Contains(presetId))
            reasons.Add($"この戦略は質感 {presetId} に対応していません。");
        if (entry.Status != TexturePresetStatus.Available) reasons.Add(entry.Reason);
        var results = entry.Strategy.Requirements.Select(r => Check(r, inventory)).ToArray();
        foreach (var unmet in results.Where(x => x.Outcome == StrategyOutcome.Unmet)) reasons.Add(unmet.Reason);
        var outcome = reasons.Count > 0 ? StrategyOutcome.Unmet
            : results.Any(x => x.Outcome == StrategyOutcome.Unknown) ? StrategyOutcome.Unknown : StrategyOutcome.Met;
        return new StrategyFit { Outcome = outcome, Results = results, UnmetReasons = reasons };
    }

    // {
    //   責務: [Candidates: 成立する(または測定待ちの)戦略を、決定的な順で返す]
    //   処理: [
    //     1: 全登録戦略を評価する
    //     2: 不成立を除く。順位は met が先、次に対象プリセットに特化した戦略、strategy_id、新しい version
    //   ]
    // }
    public static IReadOnlyList<StrategyCandidate> Candidates(
        TrainingStrategyRegistry registry, SpeakerDataInventory inventory, string target, string? presetId) =>
        registry.Entries.Select(entry => new StrategyCandidate(entry, Evaluate(entry, inventory, target, presetId)))
            .Where(x => x.Fit.Outcome != StrategyOutcome.Unmet)
            .OrderBy(x => x.Fit.Outcome == StrategyOutcome.Met ? 0 : 1)
            .ThenBy(x => presetId is not null && x.Entry.Strategy.SupportedPresetIds.Contains(presetId) ? 0 : 1)
            .ThenBy(x => x.Entry.Strategy.StrategyId, StringComparer.Ordinal)
            .ThenByDescending(x => x.Entry.Strategy.Version, Comparer<string>.Create(TexturePresetValidator.CompareVersions))
            .ToArray();

    private static RequirementResult Check(StrategyRequirement requirement, SpeakerDataInventory inventory)
    {
        var (actual, measured) = Measure(requirement, inventory);
        if (actual is null)
            return new(requirement, StrategyOutcome.Unknown, null, $"{requirement.Role} の {requirement.Metric} は測定できていません。");
        var holds = requirement.Op switch
        {
            "gte" => actual >= requirement.Value, "gt" => actual > requirement.Value,
            "lte" => actual <= requirement.Value, "lt" => actual < requirement.Value,
            _ => actual == requirement.Value
        };
        // 一部しか測れていない値は下限として扱う: 「以上」が満たされれば確かだが、満たされない場合は断定しない。
        if (!measured && !(holds && requirement.Op is "gte" or "gt"))
            return new(requirement, StrategyOutcome.Unknown, actual, $"{requirement.Role} の {requirement.Metric} は一部しか測定できておらず、条件を断定できません。");
        return holds
            ? new(requirement, StrategyOutcome.Met, actual, string.Empty)
            : new(requirement, StrategyOutcome.Unmet, actual,
                $"{requirement.Role} の {requirement.Metric} が条件を満たしません(実測 {actual}、条件 {requirement.Op} {requirement.Value})。");
    }

    // role × metric の実測値と、全件測れているか。対応しない組合せ・測れない指標は null(unknown)。
    private static (double? Actual, bool Measured) Measure(StrategyRequirement requirement, SpeakerDataInventory inventory)
    {
        var duration = inventory.MeasurementStatus.GetValueOrDefault("duration");
        var seconds = requirement.Metric == "eligible_seconds";
        if (seconds && duration == ConfidenceStatus.Unavailable) return (null, false);
        var measured = !seconds || duration == ConfidenceStatus.Measured;
        double? value = (requirement.Role, requirement.Metric) switch
        {
            ("speech_teacher", "eligible_seconds") => inventory.Speech.EligibleSeconds,
            ("speech_teacher", "independent_utterances") => inventory.Speech.IndependentUtterances,
            ("speech_teacher", "distinct_sources") => inventory.Speech.DistinctRecordings,
            ("speech_teacher", "phoneme_coverage_count") => inventory.Speech.PhonemeCoverageCount,
            ("singing_teacher", "eligible_seconds") => inventory.Singing.EligibleSeconds,
            ("singing_teacher", "independent_phrases") => inventory.Singing.IndependentPhrases,
            ("singing_teacher", "distinct_sources") => inventory.Singing.DistinctRecordings,
            ("singing_teacher", "pitch_bins_observed") => inventory.Singing.PitchBinsObserved,
            ("identity_reference", "eligible_seconds") => inventory.IdentityReference.EligibleSeconds,
            ("identity_reference", "distinct_sources") => inventory.IdentityReference.DistinctSources,
            _ => null
        };
        return (value, measured);
    }
}
