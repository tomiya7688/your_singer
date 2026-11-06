using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

public sealed record SingingReferenceSegment(string SegmentId, string SpeakerId);

// AvailableSubmodels: 提供済みの submodel(null なら BuiltInSingingStrategies.AvailableSubmodels)。
public sealed record SingingPlanRequest(
    string SpeakerId, string PresetId, string? PresetVersion, SpeakerDataInventory Inventory,
    IReadOnlyList<SingingReferenceSegment> References, IReadOnlySet<string>? AvailableSubmodels = null);

public sealed record SingingPlanStage(string StageId, string OperationId, IReadOnlyList<string> DependsOn, IReadOnlyList<string> InputRoles);

// Variant: singing_teacher_based(実測の歌唱教師を使う) / speech_timbre_reference(発話由来の声質参照を使う)。
// MissingSubmodels が空でない候補は、そのままでは歌唱モデルとして完結しない(利用者に隠さない)。
public sealed record SingingPlanCandidate(
    string Variant, TrainingStrategyReference Reference, string DisplayNameJa, string FitOutcome,
    IReadOnlyList<SingingPlanStage> Stages, IReadOnlyList<string> ReferenceSegmentIds,
    IReadOnlyList<string> MissingSubmodels, IReadOnlyList<string> Warnings);

public sealed record SingingPlanRejection(string StrategyId, string Version, IReadOnlyList<string> Reasons);

public sealed class SingingTrainingPlan
{
    // 常に singing。素材が発話優勢でも変更しない。
    public string Target => "singing";
    public required string SpeakerId { get; init; }
    public required string PresetId { get; init; }
    public required string PresetVersion { get; init; }
    public required string PresetStatus { get; init; }
    public required string InventoryRevision { get; init; }
    public required IReadOnlyList<SingingPlanCandidate> Candidates { get; init; }
    public required IReadOnlyList<SingingPlanRejection> Rejected { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public bool HasCandidates => Candidates.Count > 0;
}

// {
//   責務: [SingingTrainingStrategyPlanner: 利用者指定の歌唱目的・質感と素材量から、学習工程の候補を構成する]
//   フィールド: [_strategies: 登録済みの学習戦略, _presets: 登録済みの質感プリセット]
//   補足: [
//     候補は戦略の宣言と実測の棚卸しだけから決まる。発話優勢でも target は singing のまま。
//     発話音声から未知の音域・ビブラート・音符の教師を作る宣言(歌唱教師なしで singing_base を作る Stage)は候補にしない。
//   ]
// }
public sealed class SingingTrainingStrategyPlanner(TrainingStrategyRegistry strategies, TexturePresetRegistry presets)
{
    // {
    //   責務: [Plan: 話者・質感・棚卸し・参考区間を検証し、成立する歌唱学習の候補と、不成立の理由を返す]
    //   引数: [request: 話者・質感・棚卸し・参考区間・提供済み submodel]
    //   戻り値: [plan: 候補(空もありうる)・不成立の理由・警告]
    //   エラー: [話者の不一致・未登録または singing 非対応の質感・別話者または空の参考区間で ArgumentException]
    // }
    public SingingTrainingPlan Plan(SingingPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SpeakerId)) throw new ArgumentException("話者が指定されていません。");
        if (request.Inventory.SpeakerId != request.SpeakerId)
            throw new ArgumentException("棚卸しの話者が計画の話者と一致しません(別話者の素材量では計画しません)。");
        var preset = presets.Find(request.PresetId, request.PresetVersion)
            ?? throw new ArgumentException($"登録されていない質感プリセットです: {request.PresetId}");
        if (!preset.Preset.SupportedTargets.Contains("singing"))
            throw new ArgumentException($"{preset.Preset.DisplayNameJa} は singing に対応していません。");
        var referenceIds = ValidateReferences(request, preset);
        var submodels = request.AvailableSubmodels ?? BuiltInSingingStrategies.AvailableSubmodels;
        var missing = BuiltInSingingStrategies.RequiredSubmodels.Where(x => !submodels.Contains(x)).ToArray();

        var warnings = BuildWarnings(request.Inventory, preset);
        var order = StrategyFitEvaluator.Candidates(strategies, request.Inventory, "singing", preset.Preset.PresetId)
            .Select((x, i) => (x.Entry.Strategy.StrategyId, x.Entry.Strategy.Version, Index: i))
            .ToDictionary(x => (x.StrategyId, x.Version), x => x.Index);
        var candidates = new List<SingingPlanCandidate>();
        var rejected = new List<SingingPlanRejection>();
        foreach (var entry in strategies.Entries)
        {
            var guard = SpeechAsSingingTeacher(entry.Strategy);
            var fit = StrategyFitEvaluator.Evaluate(entry, request.Inventory, "singing", preset.Preset.PresetId);
            var reasons = guard is null ? fit.UnmetReasons : [.. fit.UnmetReasons, guard];
            if (reasons.Count > 0)
                rejected.Add(new SingingPlanRejection(entry.Strategy.StrategyId, entry.Strategy.Version, reasons));
            else
                candidates.Add(ToCandidate(entry, fit, preset, request.Inventory, referenceIds, missing));
        }
        // 成立(met)を先に、次に実測の歌唱教師を使う案(直接の根拠)を代替より先にする。以後は評価器の順。
        candidates = [.. candidates
            .OrderBy(x => x.FitOutcome == StrategyOutcome.Met ? 0 : 1)
            .ThenBy(x => x.Variant == "singing_teacher_based" ? 0 : 1)
            .ThenBy(x => order.GetValueOrDefault((x.Reference.StrategyId, x.Reference.Version), int.MaxValue))];
        return new SingingTrainingPlan
        {
            SpeakerId = request.SpeakerId, PresetId = preset.Preset.PresetId, PresetVersion = preset.Preset.Version,
            PresetStatus = preset.Status, InventoryRevision = request.Inventory.InventoryRevision,
            Candidates = candidates, Rejected = rejected, Warnings = warnings
        };
    }

    private static List<string> BuildWarnings(SpeakerDataInventory inventory, TexturePresetEntry preset)
    {
        var warnings = new List<string>();
        if (preset.Status != TexturePresetStatus.Available)
            warnings.Add($"質感「{preset.Preset.DisplayNameJa}」: {preset.Reason}");
        if (inventory.Singing.EligibleSeconds <= 0 && inventory.Speech.EligibleSeconds > 0)
            warnings.Add("歌唱教師がありません。発話からは未知の音域・ビブラート・音符の教師を作れないため、発話由来の案は声質の参照に限られ、実音声での評価が済むまで品質を保証しません。");
        var unmeasured = inventory.MeasurementStatus.Where(x => x.Value != ConfidenceStatus.Measured)
            .Select(x => x.Key).Order(StringComparer.Ordinal).ToArray();
        if (unmeasured.Length > 0)
            warnings.Add($"測定できていない指標があります(未校正のF0・不明な歌詞などを含みうる): {string.Join(", ", unmeasured)}");
        return warnings;
    }

    private static List<string> ValidateReferences(SingingPlanRequest request, TexturePresetEntry preset)
    {
        foreach (var reference in request.References)
            if (reference.SpeakerId != request.SpeakerId)
                throw new ArgumentException($"参考区間 {reference.SegmentId} は別話者のものです。同一話者の区間だけを使います。");
        var ids = request.References.Select(x => x.SegmentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (ids.Count == 0 && preset.Preset.RequiredCapabilityTags.Contains("same_speaker_reference"))
            throw new ArgumentException($"{preset.Preset.DisplayNameJa} には同一話者の参考区間が必要です。");
        return ids;
    }

    // singing_base(歌唱の基礎モデル)を、歌唱教師なしで作るStageを持つ宣言を検出する。
    private static string? SpeechAsSingingTeacher(TrainingStrategyDescriptor strategy) =>
        strategy.Stages.Any(stage => stage.OutputTypes.Contains("singing_base") && !stage.InputRoles.Contains("singing_teacher"))
            ? "発話音声から歌唱の基礎モデル(音域・音符の教師)を作る宣言は、歌唱の候補にしません。"
            : null;

    private static SingingPlanCandidate ToCandidate(
        TrainingStrategyEntry entry, StrategyFit fit, TexturePresetEntry preset, SpeakerDataInventory inventory,
        List<string> referenceIds, string[] missingSubmodels)
    {
        var usesSinging = entry.Strategy.Stages.Any(x => x.InputRoles.Contains("singing_teacher"));
        var warnings = fit.Results.Where(x => x.Outcome == StrategyOutcome.Unknown).Select(x => x.Reason).ToList();
        if (missingSubmodels.Length > 0)
            warnings.Add($"必須の submodel が未提供です: {string.Join(", ", missingSubmodels)}。これだけでは歌唱モデルとして完結しません。");
        return new SingingPlanCandidate(
            usesSinging ? "singing_teacher_based" : "speech_timbre_reference",
            TrainingStrategyRegistry.ReferenceOf(entry, preset, inventory), entry.Strategy.DisplayNameJa, fit.Outcome,
            [.. entry.Strategy.Stages.Select(x => new SingingPlanStage(x.Id, x.OperationId, x.DependsOn, x.InputRoles))],
            referenceIds, missingSubmodels, warnings);
    }
}
