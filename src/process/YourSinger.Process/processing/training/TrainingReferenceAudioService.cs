using System.Security.Cryptography;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Training;

// { 責務: [ReferenceAudioRecord: ジョブに複製した参考音声1件の記録(元の名前・複製先・SHA-256・秒数)] }
public sealed record ReferenceAudioRecord(string OriginalName, string RelativePath, string Sha256, double Seconds);

// { 責務: [ReferenceAudioRejection: 取り込まなかった参考音声と日本語の理由] }
public sealed record ReferenceAudioRejection(string OriginalName, string Reason);

// { 責務: [ReferenceAudioImport: 取り込みの結果。受け付けたものと、理由つきで断ったもの] }
public sealed record ReferenceAudioImport(IReadOnlyList<ReferenceAudioRecord> Accepted, IReadOnlyList<ReferenceAudioRejection> Rejected);

// {
//   責務: [TrainingReferenceAudioService: 利用者が指定した参考音声(声質の手本)を検査し、ジョブ専用の場所へ複製する]
//   処理: [1: 権利の確認が無ければ何も保存しない 2: wav・長さ・件数・合計の上限を検査 3: 通ったものだけ training/references/<job-id>/ へ複製しSHA-256を記録]
//   補足: [参考音声は教師データにしない。元ファイルは変更しない。同じ内容(SHA-256)は1件にまとめる。無音の検査は未実装。]
// }
public static class TrainingReferenceAudioService
{
    public const int MaxFiles = 8;
    public const double MaxSecondsPerFile = 30;
    public const double MaxTotalSeconds = 120;

    // {
    //   責務: [Import: 参考音声を検査してジョブ専用の場所へ複製する]
    //   処理: [同意確認→ジョブID検査→ファイルごとに検査→通ったものだけ複製]
    //   引数: [workspace: 対象ワークスペース, jobId: ジョブID(英数字と -_. のみ), sourcePaths: 参考音声の絶対パス, consentConfirmed: 使う権利があり本人の声か許可を得ていることの確認]
    //   戻り値: [受け付けた記録と、断った理由]
    //   副作用: [training/references/<job-id>/ にファイルを作る。名前は SHA-256 の先頭12桁なので、内容が違えば別のファイルになる]
    //   エラー: [同意が無い・ジョブIDが不正な場合は InvalidOperationException。何も保存しない]
    // }
    public static ReferenceAudioImport Import(
        ProjectWorkspace workspace, string jobId, IReadOnlyList<string> sourcePaths, bool consentConfirmed)
    {
        if (!consentConfirmed)
            throw new InvalidOperationException("参考音声を使う権利と、本人の声か許可を得ていることの確認が必要です。保存しませんでした。");
        if (string.IsNullOrWhiteSpace(jobId) || !jobId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') || jobId.All(c => c == '.'))
            throw new InvalidOperationException("ジョブIDが不正なため、参考音声を保存しませんでした。");

        var folder = Path.Combine(workspace.RootPath, "training", "references", jobId);
        var accepted = new List<ReferenceAudioRecord>();
        var rejected = new List<ReferenceAudioRejection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0.0;
        foreach (var source in sourcePaths)
        {
            var name = Path.GetFileName(source);
            var reason = Check(source, accepted.Count, total, out var seconds, out var sha);
            if (reason is null && !seen.Add(sha!)) reason = "同じ内容の音声が既に追加されています。";
            if (reason is not null) { rejected.Add(new(name, reason)); continue; }

            // 複製先は SHA-256 の先頭12桁を名前にし、別の音声同士が上書きし合わないようにする。
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"{sha![..12]}.wav");
            File.Copy(source, target, overwrite: true);
            total += seconds;
            accepted.Add(new(name, Path.GetRelativePath(workspace.RootPath, target).Replace('\\', '/'), sha, seconds));
        }
        return new(accepted, rejected);
    }

    // 1件の検査。通れば null、断るなら日本語の理由を返す。
    private static string? Check(string source, int acceptedCount, double total, out double seconds, out string? sha)
    {
        seconds = 0;
        sha = null;
        if (acceptedCount >= MaxFiles) return $"参考音声は最大{MaxFiles}件までです。";
        if (!string.Equals(Path.GetExtension(source), ".wav", StringComparison.OrdinalIgnoreCase))
            return "wav 形式のみ追加できます。";
        if (!File.Exists(source)) return "ファイルが見つかりません。";
        if (WavDurationReader.ReadFile(source) is not { } length || length <= 0)
            return "音声として読めませんでした(長さを読み取れません)。";
        if (length > MaxSecondsPerFile) return $"1件{MaxSecondsPerFile:0}秒以内にしてください(この音声は{length:0.#}秒)。";
        if (total + length > MaxTotalSeconds) return $"合計{MaxTotalSeconds:0}秒以内にしてください。";
        seconds = length;
        using var stream = File.OpenRead(source);
        sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        return null;
    }
}
