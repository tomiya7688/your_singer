using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// 参考区間1件(同一話者であることを計画時に検証する)。
public sealed record TalkReferenceSegment(string SegmentId, string SpeakerId);

public sealed record TalkPlanRequest(
    string SpeakerId, string PresetId, string? PresetVersion, SpeakerDataInventory Inventory,
    IReadOnlyList<TalkReferenceSegment> References);

public sealed record TalkPlanStage(string StageId, string OperationId, IReadOnlyList<string> DependsOn, IReadOnlyList<string> InputRoles);

// Variant: speech_teacher_based(発話教師を使う) / singing_timbre_reference(歌唱由来の声質参照を使う)。
public sealed record TalkPlanCandidate(
    string Variant, TrainingStrategyReference Reference, string DisplayNameJa, string FitOutcome,
    IReadOnlyList<TalkPlanStage> Stages, IReadOnlyList<string> ReferenceSegmentIds, IReadOnlyList<string> Warnings);

public sealed record TalkPlanRejection(string StrategyId, string Version, IReadOnlyList<string> Reasons);

public sealed class TalkTrainingPlan
{
    // 常に talk。素材が歌唱優勢でも変更しない。
    public string Target => "talk";
    public required string SpeakerId { get; init; }
    public required string PresetId { get; init; }
    public required string PresetVersion { get; init; }
    public required string PresetStatus { get; init; }
    public required string InventoryRevision { get; init; }
    public required IReadOnlyList<TalkPlanCandidate> Candidates { get; init; }
    // 候補にならなかった戦略と理由(未対応・条件不成立・歌唱を発話教師にする宣言など)。
    public required IReadOnlyList<TalkPlanRejection> Rejected { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public bool HasCandidates => Candidates.Count > 0;
}

// {
//   責務: [TalkTrainingStrategyPlanner: 利用者指定のトーク目的・質感と素材量から、学習工程の候補を構成する]
//   フィールド: [_strategies: 登録済みの学習戦略, _presets: 登録済みの質感プリセット]
//   補足: [
//     候補は戦略の宣言(requirements・stages)と実測の棚卸しだけから決まる。固定の閾値や暗黙のfallbackは持たない。
//     歌唱優勢でも target は talk のまま。歌唱音声を発話の教師にする戦略は候補にしない。
//   ]
// }
public sealed class TalkTrainingStrategyPlanner(TrainingStrategyRegistry strategies, TexturePresetRegistry presets)
{
    private static readonly string[] SpeechOutputs = ["tts_base", "talk_model"];

    // {
    //   責務: [Plan: 話者・質感・棚卸し・参考区間を検証し、成立するトーク学習の候補と、不成立の理由を返す]
    //   処理: [
    //     1: 話者・棚卸し・質感・参考区間を検証する(誤りは明示的に失敗)
    //     2: 全戦略を評価し、歌唱を発話教師にする宣言を除く
    //     3: 成立する戦略を候補に、不成立を理由付きで rejected にする
    //   ]
    //   引数: [request: 話者・質感・棚卸し・参考区間]
    //   戻り値: [plan: 候補(空もありうる)・不成立の理由・警告]
    //   エラー: [話者の不一致・未登録の質感・talk非対応の質感・別話者または空の参考区間で ArgumentException]
    // }
    public TalkTrainingPlan Plan(TalkPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SpeakerId)) throw new ArgumentException("話者が指定されていません。");
        if (request.Inventory.SpeakerId != request.SpeakerId)
            throw new ArgumentException("棚卸しの話者が計画の話者と一致しません(別話者の素材量では計画しません)。");
        var preset = presets.Find(request.PresetId, request.PresetVersion)
            ?? throw new ArgumentException($"登録されていない質感プリセットです: {request.PresetId}");
        if (!preset.Preset.SupportedTargets.Contains("talk"))
            throw new ArgumentException($"{preset.Preset.DisplayNameJa} は talk に対応していません。");
        var referenceIds = ValidateReferences(request, preset);

        var warnings = new List<string>();
        if (preset.Status != TexturePresetStatus.Available)
            warnings.Add($"質感「{preset.Preset.DisplayNameJa}」: {preset.Reason}");
        if (request.Inventory.Speech.EligibleSeconds <= 0 && request.Inventory.Singing.EligibleSeconds > 0)
            warnings.Add("発話教師がありません。歌唱由来の声質参照による案は、実音声での評価が済むまで品質を保証しません。");

        var candidates = new List<TalkPlanCandidate>();
        var rejected = new List<TalkPlanRejection>();
        foreach (var entry in strategies.Entries)
        {
            var guard = SingingAsSpeechTeacher(entry.Strategy);
            var fit = StrategyFitEvaluator.Evaluate(entry, request.Inventory, "talk", preset.Preset.PresetId);
            var reasons = guard is null ? fit.UnmetReasons : [.. fit.UnmetReasons, guard];
            if (reasons.Count > 0)
            {
                rejected.Add(new TalkPlanRejection(entry.Strategy.StrategyId, entry.Strategy.Version, reasons));
                continue;
            }
            candidates.Add(ToCandidate(entry, fit, preset, request.Inventory, referenceIds));
        }
                var order = StrategyFitEvaluator.Candidates(strategies, request.Inventory, "talk", preset.Preset.PresetId)
            .Select((x, i) => (x.Entry.Strategy.StrategyId, x.Entry.Strategy.Version, Index: i)).ToDictionary(x => (x.StrategyId, x.Version), x => x.Index);
        // 成立(met)を先に、次に発話教師を使う案(直接の根拠)を、歌唱由来の案(代替)より先にする。以後は評価器の順。
        candidates = [.. candidates
            .OrderBy(x => x.FitOutcome == StrategyOutcome.Met ? 0 : 1)
            .ThenBy(x => x.Variant == "speech_teacher_based" ? 0 : 1)
            .ThenBy(x => order.GetValueOrDefault((x.Reference.StrategyId, x.Reference.Version), int.MaxValue))];
        return new TalkTrainingPlan
        {
            SpeakerId = request.SpeakerId, PresetId = preset.Preset.PresetId, PresetVersion = preset.Preset.Version,
            PresetStatus = preset.Status, InventoryRevision = request.Inventory.InventoryRevision,
            Candidates = candidates, Rejected = rejected, Warnings = warnings
        };
    }

    // 参考区間は同一話者のものだけを受け付ける。同一性を要する質感で空なら失敗する。
    private static List<string> ValidateReferences(TalkPlanRequest request, TexturePresetEntry preset)
    {
        foreach (var reference in request.References)
            if (reference.SpeakerId != request.SpeakerId)
                throw new ArgumentException($"参考区間 {reference.SegmentId} は別話者のものです。同一話者の区間だけを使います。");
        var ids = request.References.Select(x => x.SegmentId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (ids.Count == 0 && preset.Preset.RequiredCapabilityTags.Contains("same_speaker_reference"))
            throw new ArgumentException($"{preset.Preset.DisplayNameJa} には同一話者の参考区間が必要です。");
        return ids;
    }

    // 歌唱教師だけを入力に発話モデル(tts_base/talk_model)を作るStageを持つ宣言を検出する。
    private static string? SingingAsSpeechTeacher(TrainingStrategyDescriptor strategy) =>
        strategy.Stages.Any(stage => stage.InputRoles.Contains("singing_teacher") && !stage.InputRoles.Contains("speech_teacher") &&
                                     stage.OperationId.Contains("train", StringComparison.Ordinal) &&
                                     stage.OutputTypes.Any(SpeechOutputs.Contains))
            ? "歌唱音声を通常発話の教師として学習する宣言は、トークの候補にしません。"
            : null;

    private TalkPlanCandidate ToCandidate(
        TrainingStrategyEntry entry, StrategyFit fit, TexturePresetEntry preset, SpeakerDataInventory inventory, List<string> referenceIds)
    {
        var usesSpeech = entry.Strategy.Stages.Any(x => x.InputRoles.Contains("speech_teacher"));
        var warnings = fit.Results.Where(x => x.Outcome == StrategyOutcome.Unknown).Select(x => x.Reason).ToList();
        return new TalkPlanCandidate(
            usesSpeech ? "speech_teacher_based" : "singing_timbre_reference",
            TrainingStrategyRegistry.ReferenceOf(entry, preset, inventory), entry.Strategy.DisplayNameJa, fit.Outcome,
            [.. entry.Strategy.Stages.Select(x => new TalkPlanStage(x.Id, x.OperationId, x.DependsOn, x.InputRoles))],
            referenceIds, warnings);
    }
}
