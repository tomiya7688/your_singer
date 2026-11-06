using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Training;

// Preflight が参照する実行環境(GPU と、初期重みとして使える既存ファイル)。呼び出し側が実測して渡す。
public sealed record PreflightEnvironment(HardwareInfo Hardware, IReadOnlyList<ResolverCheckpoint> Checkpoints);

// {
//   責務: [TrainingPreflightService: ジョブの目的・質感・棚卸しから学習計画を解決し、結果をジョブに固定する]
//   フィールド: [_talk/_singing: 用途別のプランナー, _jobRepository: ジョブの永続化]
//   処理: [1: 旧ジョブ・実行済みジョブを拒否する 2: 用途別プランナーで候補を作る 3: TrainingModeResolver で解決する 4: 結果を保存する]
//   補足: [
//     解決できない構成でも結果(停止理由つき)を保存し、学習は始めない。ジョブの目的と質感は変更しない。
//     解決後に素材の棚卸しが変わった場合は RequireExecutable が失敗し、再度 Preflight が必要になる。
//   ]
// }
public sealed class TrainingPreflightService(
    TalkTrainingStrategyPlanner talk, SingingTrainingStrategyPlanner singing, TrainingJobRepository jobRepository)
{
    // {
    //   責務: [PreflightAsync: 指定ジョブの学習計画を解決し、ResolvedPlan として保存する]
    //   引数: [workspace: 作業領域, batchId/jobId: 対象ジョブ, inventory: 同一話者の最新の棚卸し, environment: GPUと初期重み]
    //   戻り値: [plan: 保存した解決結果(Executable=false なら学習を始めない)]
    //   エラー: [旧ジョブ・Pending以外・話者の不一致・ジョブが無い場合に InvalidOperationException]
    // }
    public async Task<TrainingResolvedPlanRecord> PreflightAsync(
        ProjectWorkspace workspace, string batchId, string jobId, SpeakerDataInventory inventory,
        PreflightEnvironment environment, CancellationToken cancellationToken = default)
    {
        var batch = await jobRepository.LoadBatchAsync(workspace, batchId, cancellationToken)
            ?? throw new InvalidOperationException("学習ジョブのバッチがありません。");
        var job = batch.Jobs.FirstOrDefault(x => x.JobId == jobId)
            ?? throw new InvalidOperationException("学習ジョブがありません。");
        if (job.IsLegacyWithoutTrainingContract)
            throw new InvalidOperationException("学習方式の契約を持たない旧ジョブです。新しいジョブを作成してください。");
        if (job.State != TrainingJobState.Pending)
            throw new InvalidOperationException("実行前(Pending)のジョブだけPreflightできます。");
        if (inventory.SpeakerId != job.SpeakerId)
            throw new InvalidOperationException("棚卸しの話者がジョブの話者と一致しません。");

        var target = job.Target == TrainingTarget.Talk ? "talk" : "singing";
        var texture = job.RequestedTexture!;
        var (candidates, plannerReasons) = target == "talk" ? TalkCandidates(job, texture, inventory) : SingingCandidates(job, texture, inventory);
        var resolved = TrainingModeResolver.Resolve(new ModeResolutionRequest(
            target, job.SpeakerId, texture, inventory, job.RequestedTrainingMode!.Value, candidates,
            environment.Hardware, environment.Checkpoints, job.Seed));
        var record = ToRecord(resolved, plannerReasons);
        job.ResolvedPlan = record;
        await jobRepository.SaveBatchAsync(workspace, batch, cancellationToken);
        return record;
    }

    // {
    //   責務: [RequireExecutable: ジョブが実行できる解決済み計画を持ち、棚卸しが変わっていないことを確認する]
    //   エラー: [未Preflight・実行不可・棚卸しの版違いで InvalidOperationException(日本語の理由つき)]
    // }
    public static void RequireExecutable(TrainingJobRecord job, string currentInventoryRevision)
    {
        var plan = job.ResolvedPlan
            ?? throw new InvalidOperationException("学習計画がまだ解決されていません。実行前にPreflightを行ってください。");
        if (!plan.Executable)
            throw new InvalidOperationException("学習を始められません: " + string.Join(" / ", plan.StopReasons));
        if (plan.InventoryRevision != currentInventoryRevision)
            throw new InvalidOperationException("素材の棚卸しが計画の解決後に変わりました。Preflightをやり直してください。");
    }

    private (ResolverCandidate[] Candidates, string[] Reasons) TalkCandidates(
        TrainingJobRecord job, TrainingTextureRequest texture, SpeakerDataInventory inventory)
    {
        var plan = talk.Plan(new TalkPlanRequest(job.SpeakerId, PresetId(texture), texture.PresetVersion, inventory,
            [.. texture.ReferenceSegmentIds.Select(x => new TalkReferenceSegment(x, job.SpeakerId))]));
        return ([.. plan.Candidates.Select(ResolverCandidate.From)],
            [.. plan.Rejected.SelectMany(r => r.Reasons.Select(reason => $"{r.StrategyId}: {reason}"))]);
    }

    private (ResolverCandidate[] Candidates, string[] Reasons) SingingCandidates(
        TrainingJobRecord job, TrainingTextureRequest texture, SpeakerDataInventory inventory)
    {
        var plan = singing.Plan(new SingingPlanRequest(job.SpeakerId, PresetId(texture), texture.PresetVersion, inventory,
            [.. texture.ReferenceSegmentIds.Select(x => new SingingReferenceSegment(x, job.SpeakerId))]));
        return ([.. plan.Candidates.Select(ResolverCandidate.From)],
            [.. plan.Rejected.SelectMany(r => r.Reasons.Select(reason => $"{r.StrategyId}: {reason}"))]);
    }

    // ジョブの質感は "natural" のように短い名前で保存されることがあるため、組み込みIDへ正規化する。
    private static string PresetId(TrainingTextureRequest texture) =>
        texture.Preset.Contains('.') ? texture.Preset : "builtin." + texture.Preset;

    private static TrainingResolvedPlanRecord ToRecord(ResolvedTrainingPlan plan, string[] plannerReasons) => new()
    {
        ManifestHash = plan.ManifestHash, InventoryRevision = plan.InventoryRevision, Executable = plan.Executable,
        StrategyId = plan.Chosen?.Reference.StrategyId, StrategyVersion = plan.Chosen?.Reference.Version,
        Variant = plan.Chosen?.Variant, ResolvedTrainingMode = plan.ResolvedTrainingMode,
        Stages = [.. plan.Stages.Select(x => new TrainingResolvedStageRecord
        {
            StageId = x.StageId, OperationId = x.OperationId, Mode = x.Mode, Backend = x.Backend,
            BackendRevision = x.BackendRevision, Initialization = x.Initialization
        })],
        Warnings = [.. plan.Warnings],
        // 実行できないとき: プランナーが不成立とした理由と、解決段階で止めた理由の両方を残す。
        StopReasons = plan.Executable ? []
            : [.. plannerReasons, .. plan.Rejected.SelectMany(r => r.Reasons.Select(reason => $"{r.StrategyId}: {reason}"))]
    };
}
