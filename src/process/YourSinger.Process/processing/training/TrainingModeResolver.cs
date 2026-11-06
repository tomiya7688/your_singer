using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// プランナーが提案した候補1件(Talk/Singing共通の形)。
public sealed record ResolverStage(string StageId, string OperationId, IReadOnlyList<string> InputRoles);

public sealed record ResolverCandidate(
    string Target, string Variant, TrainingStrategyReference Reference,
    IReadOnlyList<ResolverStage> Stages, IReadOnlyList<string> Warnings)
{
    public static ResolverCandidate From(TalkPlanCandidate candidate) => new("talk", candidate.Variant, candidate.Reference,
        [.. candidate.Stages.Select(x => new ResolverStage(x.StageId, x.OperationId, x.InputRoles))], candidate.Warnings);

    public static ResolverCandidate From(SingingPlanCandidate candidate) => new("singing", candidate.Variant, candidate.Reference,
        [.. candidate.Stages.Select(x => new ResolverStage(x.StageId, x.OperationId, x.InputRoles))],
        [.. candidate.Warnings]);
}

// 初期重みとして使える既存ファイル。ActualSha256 は呼び出し側が実ファイルから計算した値(未計算は null)。
public sealed record ResolverCheckpoint(
    string Name, string Path, string ExpectedSha256, string? ActualSha256, bool Exists, string BackendRevision);

public sealed record HardwareInfo(bool HasCuda, int? VramMb);

// Backend の受け入れ能力(固定版)。MinVramMb が null のものは必要量が未測定で、不足を断定しない。
public sealed record BackendModeSupport(
    string Backend, string Revision, bool FineTune, bool FromScratch, bool RequiresCuda, int? MinVramMb,
    IReadOnlyList<string> CheckpointNames);

public sealed record ModeResolutionRequest(
    string Target, string SpeakerId, TrainingTextureRequest Texture, SpeakerDataInventory Inventory, TrainingMode Mode,
    IReadOnlyList<ResolverCandidate> Candidates, HardwareInfo Hardware, IReadOnlyList<ResolverCheckpoint> Checkpoints, int? Seed);

public static class ResolvedStageMode
{
    public const string FineTune = "fine_tune";
    public const string FromScratch = "from_scratch";
    public const string NotApplicable = "not_applicable";
}

public sealed record ResolvedStage(
    string StageId, string OperationId, string Mode, string? Backend, string? BackendRevision,
    TrainingInitialization? Initialization);

public sealed record ResolverRejection(string StrategyId, string Version, IReadOnlyList<string> Reasons);

public sealed class ResolvedTrainingPlan
{
    public required string Target { get; init; }
    public required string SpeakerId { get; init; }
    public required TrainingMode RequestedMode { get; init; }
    public required TrainingTextureRequest Texture { get; init; }
    public required string InventoryRevision { get; init; }
    public required int? Seed { get; init; }
    // 選ばれた候補。実行できる候補が無ければ null(Executable=false)。
    public required ResolverCandidate? Chosen { get; init; }
    public required IReadOnlyList<ResolvedStage> Stages { get; init; }
    public required IReadOnlyList<ResolverRejection> Rejected { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    // 計画の内容から決まるhash。worker は再解決せず、この内容のとおりに実行する。
    public required string ManifestHash { get; init; }
    public bool Executable => Chosen is not null;
    // 単一Trainer工程に対する互換記録(最初のTrainer工程のmode)。無ければ null。
    public string? ResolvedTrainingMode => Stages.FirstOrDefault(x => x.Mode != ResolvedStageMode.NotApplicable)?.Mode;
}

// {
//   責務: [TrainingModeResolver: プランナーの候補を、モード・初期重み・GPU・素材の条件で検証し、実行前に固定する]
//   補足: [
//     目的(target)と質感は変更しない。候補の target が要求と違えば失敗する。
//     Auto は checkpoint の有無だけで決めず、プランナーが評価済みの候補順に、各Trainer工程の条件を検証して解決する。
//     解決できない構成は学習開始前に停止し、日本語の理由を返す。暗黙のfallback(重み読込失敗→scratch等)はしない。
//   ]
// }
public static class TrainingModeResolver
{
    // Trainer工程にあたる operation と、その Backend の受け入れ能力。ここに無い operation(評価・声質追記)は mode を持たない。
    // SBV2 は現行の学習入口が事前学習重みを必須とするため FromScratch を宣言しない(#93 で初期化方式を追加するまで)。
    public static readonly IReadOnlyDictionary<string, BackendModeSupport> TrainerOperations =
        new Dictionary<string, BackendModeSupport>(StringComparer.Ordinal)
        {
            ["builtin.sbv2.train"] = new(TrainingBackendCatalog.TalkBackend, TrainingBackendCatalog.TalkRevision,
                FineTune: true, FromScratch: false, RequiresCuda: true, MinVramMb: null,
                ["G_0.safetensors", "D_0.safetensors", "DUR_0.safetensors"]),
            ["builtin.diffsinger.acoustic"] = new(TrainingBackendCatalog.SingingBackend, TrainingBackendCatalog.SingingRevision,
                FineTune: true, FromScratch: true, RequiresCuda: true, MinVramMb: null, ["acoustic"])
        };

    // {
    //   責務: [Resolve: 候補を順に検証し、最初に実行できる候補を各工程のモード・初期化つきで確定する]
    //   処理: [
    //     1: 要求(target・話者・質感・候補の target)を検証する
    //     2: 候補ごとに、Trainer工程のモード・checkpoint・GPU・教師量を検証する
    //     3: 最初に全工程が解決できた候補を選び、manifest hash を計算する。無ければ全候補の停止理由を返す
    //   ]
    //   エラー: [target不一致・質感のpreset未指定・棚卸しの話者不一致で ArgumentException]
    // }
    public static ResolvedTrainingPlan Resolve(ModeResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Target is not ("talk" or "singing")) throw new ArgumentException($"未知のtargetです: {request.Target}");
        if (string.IsNullOrWhiteSpace(request.Texture.Preset)) throw new ArgumentException("質感が指定されていません。");
        if (request.Inventory.SpeakerId != request.SpeakerId)
            throw new ArgumentException("棚卸しの話者が計画の話者と一致しません。");
        foreach (var candidate in request.Candidates)
            if (candidate.Target != request.Target)
                throw new ArgumentException($"候補の目的 {candidate.Target} が要求 {request.Target} と違います(目的は素材に合わせて変えません)。");

        var rejected = new List<ResolverRejection>();
        foreach (var candidate in request.Candidates)
        {
            var stopReasons = new List<string>();
            var warnings = new List<string>(candidate.Warnings);
            var stages = candidate.Stages.Select(stage => ResolveStage(stage, request, stopReasons, warnings)).ToArray();
            if (stopReasons.Count > 0)
            {
                rejected.Add(new ResolverRejection(candidate.Reference.StrategyId, candidate.Reference.Version, stopReasons));
                continue;
            }
            return Build(request, candidate, stages, rejected, warnings);
        }
        return Build(request, null, [], rejected, []);
    }

    private static ResolvedStage ResolveStage(
        ResolverStage stage, ModeResolutionRequest request, List<string> stopReasons, List<string> warnings)
    {
        if (!TrainerOperations.TryGetValue(stage.OperationId, out var support))
            return new ResolvedStage(stage.StageId, stage.OperationId, ResolvedStageMode.NotApplicable, null, null, null);

        var before = stopReasons.Count;
        CheckHardware(stage, support, request.Hardware, stopReasons, warnings);
        CheckTeacherData(stage, request.Inventory, stopReasons);
        var components = support.CheckpointNames.Select(name => request.Checkpoints.FirstOrDefault(x => x.Name == name)).ToArray();
        var problems = CheckpointProblems(stage, support, support.CheckpointNames, components);

        var mode = request.Mode switch
        {
            TrainingMode.FineTune => ResolveFineTune(stage, support, problems, stopReasons),
            TrainingMode.FromScratch => ResolveFromScratch(stage, support, stopReasons),
            _ => ResolveAuto(stage, support, problems, stopReasons, warnings)
        };
        // この工程が停止理由を増やした場合、候補は不採用になる。初期化情報は作らない。
        if (mode is null || stopReasons.Count > before)
            return new ResolvedStage(stage.StageId, stage.OperationId, ResolvedStageMode.NotApplicable, support.Backend, support.Revision, null);
        var initialization = mode == ResolvedStageMode.FineTune
            ? new TrainingInitialization
            {
                Type = TrainingJobVocabulary.PretrainedCheckpoint,
                Components = [.. components.Select(x => new TrainingInitializationComponent { Name = x!.Name, Sha256 = x.ExpectedSha256, Path = x.Path })]
            }
            : new TrainingInitialization { Type = TrainingJobVocabulary.RandomInitialization };
        return new ResolvedStage(stage.StageId, stage.OperationId, mode, support.Backend, support.Revision, initialization);
    }

    private static void CheckHardware(
        ResolverStage stage, BackendModeSupport support, HardwareInfo hardware, List<string> stopReasons, List<string> warnings)
    {
        if (support.RequiresCuda && !hardware.HasCuda)
            stopReasons.Add($"Stage {stage.StageId}: {support.Backend} の学習にはCUDA対応GPUが必要です。CPUだけでは学習を始めません。");
        else if (support.MinVramMb is { } need)
        {
            if (hardware.VramMb is null) warnings.Add($"Stage {stage.StageId}: GPUメモリ量を測定できていません(必要 {need}MB)。");
            else if (hardware.VramMb < need) stopReasons.Add($"Stage {stage.StageId}: GPUメモリが不足しています(必要 {need}MB、実測 {hardware.VramMb}MB)。");
        }
        else if (support.RequiresCuda && hardware.VramMb is null)
            warnings.Add($"Stage {stage.StageId}: GPUメモリ量を測定できていません。必要量は未測定のため、不足は判定していません。");
    }

    private static void CheckTeacherData(ResolverStage stage, SpeakerDataInventory inventory, List<string> stopReasons)
    {
        if (stage.InputRoles.Contains("speech_teacher") && inventory.Speech.EligibleSeconds <= 0)
            stopReasons.Add($"Stage {stage.StageId}: 発話教師が空です(適格な発話が0秒)。");
        if (stage.InputRoles.Contains("singing_teacher") && inventory.Singing.EligibleSeconds <= 0)
            stopReasons.Add($"Stage {stage.StageId}: 歌唱教師が空です(適格な歌唱が0秒)。");
    }

    // 初期重みの問題(欠損・hash不一致・版の非互換)。空なら全て使える。
    private static List<string> CheckpointProblems(
        ResolverStage stage, BackendModeSupport support, IReadOnlyList<string> names, ResolverCheckpoint?[] components)
    {
        var problems = new List<string>();
        for (var index = 0; index < names.Count; index++)
        {
            var checkpoint = components[index];
            if (checkpoint is null || !checkpoint.Exists)
                problems.Add($"Stage {stage.StageId}: 初期重み {names[index]} が見つかりません。");
            else if (checkpoint.BackendRevision != support.Revision)
                problems.Add($"Stage {stage.StageId}: 初期重み {names[index]} は別の版({checkpoint.BackendRevision})用で、{support.Backend} の固定版と互換がありません。");
            else if (!string.Equals(checkpoint.ActualSha256, checkpoint.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"Stage {stage.StageId}: 初期重み {names[index]} のhashが一致しません(期待 {checkpoint.ExpectedSha256}、実測 {checkpoint.ActualSha256 ?? "未計算"})。");
        }
        return problems;
    }

    private static string? ResolveFineTune(ResolverStage stage, BackendModeSupport support, List<string> problems, List<string> stopReasons)
    {
        if (!support.FineTune) stopReasons.Add($"Stage {stage.StageId}: {support.Backend} は追加学習(fine_tune)に対応していません。");
        stopReasons.AddRange(problems);
        return ResolvedStageMode.FineTune;
    }

    // from_scratch の計画は初期重みを持たない(Initialization は random_initialization で components 空)。
    // 環境に存在する事前学習重みは「使える候補」であって指定ではないため、あっても拒否しない。ジョブ作成時に from_scratch への重み指定は検証済み。
    private static string? ResolveFromScratch(ResolverStage stage, BackendModeSupport support, List<string> stopReasons)
    {
        if (!support.FromScratch)
            stopReasons.Add($"Stage {stage.StageId}: {support.Backend} の固定版は最初からの新規学習(from_scratch)に対応していません。");
        return ResolvedStageMode.FromScratch;
    }

    // Auto: 使える初期重みがあれば追加学習、無ければ新規学習。どちらも使えなければ停止。無言の切替はせず警告を残す。
    private static string? ResolveAuto(
        ResolverStage stage, BackendModeSupport support, List<string> problems, List<string> stopReasons, List<string> warnings)
    {
        if (support.FineTune && problems.Count == 0) return ResolvedStageMode.FineTune;
        if (support.FromScratch)
        {
            warnings.Add($"Stage {stage.StageId}: 使える初期重みが無いため、最初からの新規学習にします。少量の素材では品質を保証しません。");
            return ResolvedStageMode.FromScratch;
        }
        stopReasons.Add($"Stage {stage.StageId}: 自動では決められません。追加学習の初期重みが使えず、新規学習にも対応していません。");
        stopReasons.AddRange(problems);
        return ResolvedStageMode.FineTune;
    }

    private static ResolvedTrainingPlan Build(
        ModeResolutionRequest request, ResolverCandidate? chosen, ResolvedStage[] stages,
        List<ResolverRejection> rejected, List<string> warnings) => new()
    {
        Target = request.Target, SpeakerId = request.SpeakerId, RequestedMode = request.Mode, Texture = request.Texture,
        InventoryRevision = request.Inventory.InventoryRevision, Seed = request.Seed, Chosen = chosen, Stages = stages,
        Rejected = rejected, Warnings = warnings, ManifestHash = Hash(request, chosen, stages)
    };

    // 目的・質感・参考区間・棚卸し・戦略・各工程のモードと初期重みから決まる。順序の影響を受ける要素は正規化する。
    private static string Hash(ModeResolutionRequest request, ResolverCandidate? chosen, ResolvedStage[] stages)
    {
        var inv = CultureInfo.InvariantCulture;
        var canonical = string.Join('\n', request.Target, request.SpeakerId, request.Mode,
            request.Texture.Preset, request.Texture.PresetVersion ?? "none",
            string.Join(',', request.Texture.ReferenceSegmentIds.Order(StringComparer.Ordinal)),
            request.Inventory.InventoryRevision, request.Seed?.ToString(inv) ?? "none",
            chosen is null ? "none" : $"{chosen.Reference.StrategyId}@{chosen.Reference.Version}#{chosen.Reference.Hash}",
            string.Join(';', stages.Select(x =>
                $"{x.StageId}|{x.OperationId}|{x.Mode}|{x.Backend}|{x.BackendRevision}|" +
                string.Join(',', (x.Initialization?.Components ?? []).Select(c => $"{c.Name}:{c.Sha256}")))));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
