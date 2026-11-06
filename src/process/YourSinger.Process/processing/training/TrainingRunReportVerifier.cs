using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [TrainingRunReportVerifier: worker の実行記録を、ジョブの解決済み計画と実ファイルに照合する]
//   補足: [
//     checkpoint が出来ていても、指定したモード・seed・初期重みと記録が違えば失敗にする。
//     記録が無い・旧形式(schema_version なし)の応答は、新仕様の成功に読み替えず失敗にする。
//     初期重みの実読込が観測できていない(null)場合は、矛盾がなくても Unverified にとどめる。
//   ]
// }
public static class TrainingRunReportVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    // {
    //   責務: [Parse: worker 応答の run_report 要素を TrainingRunReport にする]
    //   戻り値: [report: 読み取った記録。要素が無い・形式不正なら null]
    // }
    public static TrainingRunReport? Parse(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value) return null;
        try
        {
            return value.Deserialize<TrainingRunReport>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // {
    //   責務: [Verify: 記録をジョブと出力先の実ファイルに照合し、結果を返す]
    //   引数: [job: 解決済み計画つきのジョブ, report: worker の記録(無ければnull), outputDirectory: 学習出力ディレクトリ]
    //   戻り値: [record: verified / failed / unverified と理由(日本語)]
    // }
    public static TrainingRunVerificationRecord Verify(TrainingJobRecord job, TrainingRunReport? report, string outputDirectory)
    {
        var problems = new List<string>();
        if (report is null || report.SchemaVersion != TrainingRunReport.CurrentSchemaVersion)
            return Failed("workerの実行記録がありません、または旧形式です。新しい仕様の成功としては扱えません。");
        if (report.JobId != job.JobId) problems.Add("実行記録のジョブIDがジョブと一致しません。");
        if (report.TrainerExitCode != 0) problems.Add($"Trainerの終了コードが0ではありません: {report.TrainerExitCode?.ToString() ?? "未記録"}");
        if (job.Seed is { } seed && report.Seed != seed) problems.Add($"seedが指定と違います(指定 {seed}、記録 {report.Seed?.ToString() ?? "なし"})。");
        VerifyMode(job, report, problems);
        VerifyArtifacts(report, outputDirectory, problems);
        if (problems.Count > 0) return Failed([.. problems]);
        // 起動前に置いた重みのhash確認(staged)は、Trainerが実際に読み込んだこと(loaded)の確認ではない。
        var unverified = job.ResolvedPlan?.ResolvedTrainingMode is not null &&
            (report.InitializationObserved is null || report.InitializationEvidence != TrainingInitializationEvidence.Loaded);
        return new TrainingRunVerificationRecord
        {
            Status = unverified ? TrainingRunVerificationStatus.Unverified : TrainingRunVerificationStatus.Verified,
            Problems = !unverified ? [] : report.InitializationObserved is null
                ? ["初期重みの実際の読込が観測できていないため、指定どおりの初期化は確認できていません。"]
                : ["初期重みは起動前に置いた状態のhashまで確認しました。Trainerが実際に読み込んだことは未確認です。"]
        };
    }

    private static void VerifyMode(TrainingJobRecord job, TrainingRunReport report, List<string> problems)
    {
        var planned = job.ResolvedPlan?.ResolvedTrainingMode;
        if (planned is null) return;
        if (report.ResolvedMode != planned)
            problems.Add($"学習モードが計画と違います(計画 {planned}、記録 {report.ResolvedMode ?? "なし"})。暗黙のfallbackは認めません。");
        var observed = report.InitializationObserved;
        if (observed is null) return;
        if (planned == "from_scratch" && (observed.Type != TrainingJobVocabulary.RandomInitialization || observed.Components.Count > 0))
            problems.Add("from_scratch の計画なのに、初期重みを読み込んでいます。");
        if (planned == "fine_tune")
        {
            var expected = job.ResolvedPlan!.Stages.FirstOrDefault(x => x.Initialization is not null)?.Initialization?.Components ?? [];
            var expectedKeys = expected.Select(x => $"{x.Name}:{x.Sha256.ToLowerInvariant()}").Order(StringComparer.Ordinal);
            var actualKeys = observed.Components.Select(x => $"{x.Name}:{x.Sha256.ToLowerInvariant()}").Order(StringComparer.Ordinal);
            if (observed.Type != TrainingJobVocabulary.PretrainedCheckpoint || !expectedKeys.SequenceEqual(actualKeys))
                problems.Add("実際に読み込んだ初期重みが、計画の初期重み(名前とhash)と一致しません。");
        }
    }

    private static void VerifyArtifacts(TrainingRunReport report, string outputDirectory, List<string> problems)
    {
        if (report.Artifacts.Count == 0)
        {
            problems.Add("成果物が記録されていません(空の成果物は成功にしません)。");
            return;
        }
        var root = Path.GetFullPath(outputDirectory);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        foreach (var artifact in report.Artifacts)
        {
            if (Path.IsPathRooted(artifact.Path) || artifact.Path.Split('/', '\\').Contains(".."))
            {
                problems.Add($"成果物のパスが相対パスではありません: {artifact.Path}");
                continue;
            }
            var full = Path.GetFullPath(artifact.Path, root);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) problems.Add($"成果物が出力先の外を指しています: {artifact.Path}");
            else if (!File.Exists(full) || new FileInfo(full).Length == 0) problems.Add($"成果物がありません、または空です: {artifact.Path}");
            else if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))) != artifact.Sha256.ToLowerInvariant())
                problems.Add($"成果物のhashが記録と一致しません: {artifact.Path}");
        }
    }

    private static TrainingRunVerificationRecord Failed(params string[] problems) =>
        new() { Status = TrainingRunVerificationStatus.Failed, Problems = [.. problems] };
}
