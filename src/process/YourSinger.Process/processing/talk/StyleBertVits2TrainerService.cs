using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Talk;

// {
//   責務: [StyleBertVits2TrainerService: トーク学習ジョブの入力を専用Trainerへ渡す]
//   フィールド: [_workerClient: 学習要求を送るMLワーカー]
// }
public sealed class StyleBertVits2TrainerService
{
    private readonly MlWorkerClient _workerClient;

    // { 責務: [StyleBertVits2TrainerService: 学習要求の送信先を保持する] }
    public StyleBertVits2TrainerService(MlWorkerClient workerClient)
    {
        _workerClient = workerClient;
    }

    // {
    //   責務: [TrainAsync: 作成済みのトーク学習入力でTrainerを実行する]
    //   処理: [1: ジョブと入力を確認する 2: 専用環境へ入力を渡す 3: ログの参照を返す]
    //   エラー: [入力不足・Trainer失敗・成果物不足で失敗する]
    // }
    public async Task<string> TrainAsync(
        ProjectWorkspace workspace,
        TrainingJobRecord job,
        string modelName,
        CancellationToken cancellationToken = default,
        TalkTimbreAdaptationInput? timbre = null)
    {
        if (job.Target != TrainingTarget.Talk)
            throw new InvalidOperationException(
                "トーク学習ジョブのみStyle-Bert-VITS2 trainerを実行できます。");
        // 学習前に、解決済みで実行可能な計画を要求する(未解決・実行不可は起動しない)。
        TrainingPreflightService.RequireResolvedPlan(job);

        var trainerRoot = Path.Combine(
            AppContext.BaseDirectory,
            "workers",
            "style-bert-vits2");

        var datasetDirectory = Path.Combine(
            workspace.RootPath,
            "training",
            "style-bert-vits2",
            TrainingInputDirectory.RequireComponent(job.JobId));
        TrainingInputDirectory.RequireComponent(modelName);
        if (!File.Exists(Path.Combine(datasetDirectory, "dataset.json")) ||
            !File.Exists(Path.Combine(datasetDirectory, "esd.list")))
            throw new InvalidOperationException("トーク学習入力を作成してから学習を実行してください。");
        var outputDirectory = Path.Combine(datasetDirectory, "trainer");
        var references = ResolveTimbreReferences(workspace, timbre);

        var result = await _workerClient.SendAsync(
            "train_style_bert_vits2",
            new
            {
                resolved_training_mode = job.ResolvedPlan?.ResolvedTrainingMode,
                initialization = InitializationPayload(job),
                seed = job.Seed,
                timbre_references = references,
                timbre_style_name = TalkTimbreAdaptationInput.StyleName,
                trainer_root = trainerRoot,
                model_name = modelName,
                dataset_dir = datasetDirectory,
                job_id = job.JobId,
                dataset_fingerprint = job.DatasetFingerprint,
                auto_correction_enabled = job.AutoCorrectionEnabled,
                output_dir = outputDirectory
            },
            cancellationToken);

        if (!result.TryGetProperty("log_path", out JsonElement logPath))
            throw new InvalidOperationException(
                "Style-Bert-VITS2 trainer結果にlog_pathがありません。");

        RecordTimbreUsage(job, timbre, references, result);

        // worker の実行記録を、計画と実ファイルに照合する。矛盾・欠損は失敗にし、成功として残さない。
        TrainingRunReportVerifier.VerifyAndRecord(job, result, outputDirectory);

        return logPath.GetString()
            ?? throw new InvalidOperationException(
                "Style-Bert-VITS2 trainer log pathが空です。");
    }

    // {
    //   責務: [InitializationPayload: 解決済み計画の初期化方式を、workerへ渡す形にする]
    //   戻り値: [payload: type と components(name・sha256・path)。計画が無ければ null(旧形式の要求)]
    // }
    public static object? InitializationPayload(TrainingJobRecord job)
    {
        var initialization = job.ResolvedPlan?.Stages.FirstOrDefault(x => x.Initialization is not null)?.Initialization;
        return initialization is null ? null : new
        {
            type = initialization.Type,
            components = initialization.Components.Select(x => new { name = x.Name, sha256 = x.Sha256, path = x.Path })
        };
    }

    // {
    //   責務: [ResolveTimbreReferences: 声質参照の音声をワークスペース内の絶対パスへ解決する]
    //   引数: [workspace: 対象ワークスペース, timbre: 声質参照の入力(無ければnull)]
    //   戻り値: [絶対パスの一覧(参照が無ければ空)]
    //   エラー: [ワークスペース外を指す参照を拒否する]
    // }
    public static List<string> ResolveTimbreReferences(ProjectWorkspace workspace, TalkTimbreAdaptationInput? timbre)
    {
        var root = Path.GetFullPath(workspace.RootPath);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var resolved = new List<string>();
        foreach (var reference in timbre?.References ?? [])
        {
            var full = Path.GetFullPath(reference.AudioPath, root);
            if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("声質参照の音声がワークスペース外です。");
            resolved.Add(full);
        }
        return resolved;
    }

    // {
    //   責務: [RecordTimbreUsage: Trainerの報告で確認できた場合だけ、声質参照の利用をジョブに記録する]
    //   エラー: [参照を渡したのに報告が無い場合に失敗する(使ったと偽らない)]
    // }
    public static void RecordTimbreUsage(
        TrainingJobRecord job, TalkTimbreAdaptationInput? timbre, List<string> references, JsonElement result)
    {
        if (timbre is null) return;
        var usage = timbre.Usage;
        if (references.Count > 0)
        {
            if (!result.TryGetProperty("timbre_style", out var report) ||
                !report.TryGetProperty("reference_count", out var count))
                throw new InvalidOperationException("声質参照を渡しましたが、Trainerがスタイル追加を報告していません。");
            usage = new SharedFeatureUsageEntry
            {
                Feature = usage.Feature, Domain = usage.Domain, Support = usage.Support, Used = true,
                Reason = $"歌唱区間 {count.GetInt32()} 件の音声から作ったスタイル「{TalkTimbreAdaptationInput.StyleName}」をトークモデルへ追加しました(教師データには使っていません)。"
            };
        }
        job.SharedFeatureUsage = (job.SharedFeatureUsage ?? [])
            .Where(entry => entry.Feature != usage.Feature).Append(usage).ToList();
    }
}
