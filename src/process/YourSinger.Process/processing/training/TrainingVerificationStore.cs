using System.Security.Cryptography;
using System.Text.Json;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Training;

public enum VerificationLoadStatus { Missing, Invalid, Loaded }

public enum SampleStatus { Playable, FileMissing, HashMismatch, HashNotRecorded, OutsideVerificationFolder }

// {
//   責務: [TrainingVerificationLoad: 検証記録の読み取り結果]
//   フィールド: [Status: 記録なし/不正/読み込み済み, Record: 読み込み済みの記録, Message: 不正の理由, Directory: 検証ディレクトリ]
// }
public sealed record TrainingVerificationLoad(VerificationLoadStatus Status, TrainingVerificationRecord? Record, string? Message, string Directory);

// {
//   責務: [VerificationSample: 試聴できる(またはできない)保存済みテスト音声]
//   フィールド: [Label: 表示名, FullPath: 解決済みの絶対パス(範囲外は空), Status: 再生可否の理由]
// }
public sealed record VerificationSample(string Label, string FullPath, SampleStatus Status);

// {
//   責務: [TrainingVerificationStore: ProjectWorkspace配下の検証記録と試験音声を安全に読む]
//   処理: [1: ジョブIDを検証して training/verification/<job-id> を決める 2: verification.json を読み版・ジョブIDを照合する 3: 試験音声のパス範囲・存在・ハッシュを確認する]
//   補足: [読むだけで書かない。記録が無い・壊れている場合に「検証済み」を作らない。]
// }
public static class TrainingVerificationStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };

    // {
    //   責務: [DirectoryOf: ジョブの検証ディレクトリを返す]
    //   処理: [英数字と - _ . だけのIDを受け付け、区切り文字や .. を含むIDは拒否する]
    //   引数: [workspace: プロジェクト, jobId: ジョブ識別子]
    //   戻り値: [directory: 絶対パス。識別子が不正なら null]
    // }
    public static string? DirectoryOf(ProjectWorkspace workspace, string jobId)
    {
        if (string.IsNullOrEmpty(jobId) || jobId is "." or ".."
            || !jobId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            return null;
        return Path.Combine(workspace.RootPath, "training", "verification", jobId);
    }

    // {
    //   責務: [Load: 検証記録を読む]
    //   処理: [記録なしは Missing(旧履歴は未検証), 構文・版・ジョブID不一致は Invalid]
    //   引数: [workspace: プロジェクト, jobId: ジョブ識別子]
    //   戻り値: [load: 読み取り結果]
    //   エラー: [読み取り失敗は Invalid にして理由を返す(例外にしない)]
    // }
    public static TrainingVerificationLoad Load(ProjectWorkspace workspace, string jobId)
    {
        var dir = DirectoryOf(workspace, jobId);
        if (dir is null) return new(VerificationLoadStatus.Invalid, null, "ジョブ識別子が不正です。", "");
        var file = Path.Combine(dir, "verification.json");
        if (!File.Exists(file)) return new(VerificationLoadStatus.Missing, null, null, dir);
        try
        {
            var record = JsonSerializer.Deserialize<TrainingVerificationRecord>(File.ReadAllText(file), Options);
            if (record is null) return new(VerificationLoadStatus.Invalid, null, "検証記録が空です。", dir);
            if (record.SchemaVersion != TrainingVerificationRecord.SupportedSchemaVersion)
                return new(VerificationLoadStatus.Invalid, null, $"検証記録の版 {record.SchemaVersion} には対応していません。", dir);
            if (record.JobId != jobId) return new(VerificationLoadStatus.Invalid, null, "検証記録のジョブが一致しません。", dir);
            return new(VerificationLoadStatus.Loaded, record, null, dir);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(VerificationLoadStatus.Invalid, null, "検証記録を読み込めません。", dir);
        }
    }

    // {
    //   責務: [Samples: 保存済みの試験音声(sample_wav)を再生可否つきで列挙する]
    //   処理: [検証ディレクトリ外・絶対パス・.. は範囲外, 無ければ欠損, 記録ハッシュと異なれば不一致]
    //   引数: [load: Load の結果]
    //   戻り値: [samples: 成果物順の一覧]
    // }
    public static IReadOnlyList<VerificationSample> Samples(TrainingVerificationLoad load)
    {
        if (load.Record is null) return [];
        var root = Path.GetFullPath(load.Directory) + Path.DirectorySeparatorChar;
        var list = new List<VerificationSample>();
        foreach (var artifact in load.Record.Artifacts.Where(x => x.Kind == "sample_wav"))
        {
            var label = string.IsNullOrWhiteSpace(artifact.Label) ? artifact.Path : artifact.Label!;
            var full = Path.IsPathRooted(artifact.Path) ? "" : Path.GetFullPath(Path.Combine(load.Directory, artifact.Path));
            if (full.Length == 0 || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new(label, "", SampleStatus.OutsideVerificationFolder));
                continue;
            }
            if (!File.Exists(full)) { list.Add(new(label, full, SampleStatus.FileMissing)); continue; }
            if (string.IsNullOrEmpty(artifact.AudioSha256)) { list.Add(new(label, full, SampleStatus.HashNotRecorded)); continue; }
            using var stream = File.OpenRead(full);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            list.Add(new(label, full, string.Equals(actual, artifact.AudioSha256, StringComparison.OrdinalIgnoreCase)
                ? SampleStatus.Playable : SampleStatus.HashMismatch));
        }
        return list;
    }
}
