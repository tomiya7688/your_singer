using System.Security.Cryptography;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Processing.Training;

// 学習用Weight manifestの作成・検証・hash。Trainer別のBuilder(SBV2 / DiffSinger)が共通で使う。
public static class TrainingWeightManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    // {
    //   責務: [Build: 評価から、学習に使う区間のweightを1対1で持つmanifestを作る]
    //   処理: [
    //     1: 評価がジョブと一致し、fingerprintが記録と合うことを確認する(旧ジョブ・差し替えを拒否)
    //     2: 評価にある eligible 区間だけを学習itemにし、それ以外は理由付きで skipped にする
    //     3: 対象区間に評価が無い・重みが範囲外・音声名が重複する場合は拒否する
    //   ]
    //   戻り値: [manifest と、学習に使う区間ID(SegmentId昇順)]
    // }
    public static (TrainingWeightManifest Manifest, IReadOnlyList<string> UsedSegmentIds) Build(
        TrainingJobRecord job,
        TrainingAssessmentRecord? assessment,
        IReadOnlyCollection<string> candidateSegmentIds,
        Func<string, string> audioFileNameOf)
    {
        if (job.IsLegacyWithoutTrainingContract || string.IsNullOrEmpty(job.InputFingerprint) || assessment is null)
            throw new InvalidOperationException("このジョブは学習入力の評価を持たない旧形式です。ジョブを再作成してください。");
        if (assessment.JobId != job.JobId || assessment.SpeakerId != job.SpeakerId ||
            assessment.SourceDatasetFingerprint != job.DatasetFingerprint)
            throw new InvalidDataException("評価がジョブと一致しません。ジョブを再作成してください。");
        var expected = ComputeFingerprint(job, assessment);
        if (!string.Equals(expected, job.InputFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("評価の内容がジョブ作成時から変わっています。ジョブを再作成してください。");

        var byId = assessment.Items.ToDictionary(x => x.SegmentId, StringComparer.Ordinal);
        var items = new List<TrainingWeightManifestItem>();
        var skipped = new List<TrainingWeightManifestSkipped>();
        foreach (var id in candidateSegmentIds.Order(StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(id, out var item))
                throw new InvalidDataException($"区間 {id} の評価がありません。ジョブを再作成してください。");
            if (item.Eligibility != "eligible")
            {
                skipped.Add(new() { SegmentId = id, Eligibility = item.Eligibility, ReasonCodes = [.. item.ReasonCodes] });
                continue;
            }
            items.Add(new() { SegmentId = id, AudioFilename = audioFileNameOf(id), TrainingWeight = item.TrainingWeight });
        }
        if (items.Count == 0)
            throw new InvalidOperationException("学習に使える(eligible)区間がありません。");
        var manifest = new TrainingWeightManifest
        {
            SchemaVersion = TrainingWeightManifest.CurrentSchemaVersion, JobId = job.JobId,
            PolicyVersion = assessment.PolicyVersion, InputFingerprint = job.InputFingerprint,
            Items = items, Skipped = skipped
        };
        Validate(manifest, items.Select(x => x.AudioFilename).ToArray());
        return (manifest, items.Select(x => x.SegmentId).ToArray());
    }

    // {
    //   責務: [Validate: manifestが入力一覧と1対1で、weightが範囲内かを検証する]
    //   エラー: [欠番・重複・範囲外・不正な音声名のとき InvalidDataException]
    // }
    public static void Validate(TrainingWeightManifest manifest, IReadOnlyCollection<string> trainingAudioFilenames)
    {
        if (manifest.Items is null || manifest.Items.Count == 0) throw new InvalidDataException("manifestのitemが空です。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var item in manifest.Items)
        {
            if (previous is not null && string.CompareOrdinal(previous, item.SegmentId) >= 0)
                throw new InvalidDataException($"manifestの区間が昇順でない、または重複しています: {item.SegmentId}");
            previous = item.SegmentId;
            ids.Add(item.SegmentId);
            if (!double.IsFinite(item.TrainingWeight) ||
                item.TrainingWeight < TrainingWeightPolicy.MinWeight || item.TrainingWeight > TrainingWeightPolicy.MaxWeight)
                throw new InvalidDataException($"{item.SegmentId}: weightが範囲外です({TrainingWeightPolicy.MinWeight}〜{TrainingWeightPolicy.MaxWeight})。");
            TrainingInputDirectory.RequireComponent(item.AudioFilename);
            if (!names.Add(item.AudioFilename))
                throw new InvalidDataException($"音声ファイル名が重複しています: {item.AudioFilename}");
        }
        var training = new HashSet<string>(trainingAudioFilenames, StringComparer.Ordinal);
        if (!training.SetEquals(names))
            throw new InvalidDataException("学習に使う音声とmanifestのitemが一致しません(欠落または余分があります)。");
    }

    // 保存した内容そのもののSHA-256(小文字16進)。datasetの記録と照合してmanifestの改変を検出する。
    public static async Task<string> WriteAsync(string path, TrainingWeightManifest manifest, CancellationToken cancellationToken)
    {
        await using (var stream = File.Create(path))
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
        return await HashFileAsync(path, cancellationToken);
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    // {
    //   責務: [VerifyAsync: 公開済みmanifestの存在・hash・内容を検証して返す]
    //   エラー: [無い・hashが違う・内容が不正なとき InvalidDataException]
    // }
    public static async Task<TrainingWeightManifest> VerifyAsync(
        string path, string expectedSha256, IReadOnlyCollection<string> trainingAudioFilenames, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new InvalidDataException("weight manifestがありません。");
        var actual = await HashFileAsync(path, cancellationToken);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("weight manifestのhashが記録と一致しません(改変の可能性があります)。");
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<TrainingWeightManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("weight manifestが空です。");
        Validate(manifest, trainingAudioFilenames);
        return manifest;
    }

    private static string ComputeFingerprint(TrainingJobRecord job, TrainingAssessmentRecord assessment) =>
        TrainingInputFingerprint.Compute(new TrainingInputFingerprintInput
        {
            DatasetFingerprint = job.DatasetFingerprint,
            ContractFingerprint = TrainingJobContractFingerprint.Compute(job),
            Backend = job.Backend ?? throw new InvalidDataException("ジョブにbackendの記録がありません。"),
            BackendRevision = job.BackendRevision ?? throw new InvalidDataException("ジョブにbackend revisionの記録がありません。"),
            Assessment = assessment
        });
}
