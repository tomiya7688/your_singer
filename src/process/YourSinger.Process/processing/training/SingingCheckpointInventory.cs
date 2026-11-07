using System.Security.Cryptography;
using System.Text.Json;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [SingingCheckpointInventory: 歌唱(DiffSinger acoustic)の事前学習重みを、記録ファイルのhashと実ファイルのhashで棚卸しする]
//   処理: [1: 重みの置き場の checkpoints.json(版・名前・ファイル・sha256)を読む 2: 実ファイルのsha256を計算する]
//   補足: [
//     この版には歌唱の事前学習重みを同梱していないため、記録ファイルが無い場合は空の棚卸し(重み無し)を返し、
//     fine_tune は「初期重みが見つかりません」で停止し、from_scratch だけが成立する。無言で重みを探して補うことはしない。
//     記録ファイルの版が固定版と違う重みは、Preflight が互換なしとして停止理由にする。
//   ]
// }
public static class SingingCheckpointInventory
{
    public const string ManifestName = "checkpoints.json";
    public const string AcousticName = "acoustic";

    // {
    //   責務: [ScanAsync: Preflightに渡す歌唱の初期重みの棚卸しを作る]
    //   引数: [directory: 重みの置き場(checkpoints.jsonと重みファイルを含む)]
    //   戻り値: [checkpoints: 記録された重みごとの ResolverCheckpoint。記録ファイルが無ければ空]
    //   エラー: [記録ファイルが壊れている・必須項目が無いとき InvalidDataException(空の棚卸しで通さない)]
    // }
    public static async Task<IReadOnlyList<ResolverCheckpoint>> ScanAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        var manifest = System.IO.Path.Combine(directory, ManifestName);
        if (!File.Exists(manifest)) return [];
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(manifest, cancellationToken));
            var root = document.RootElement;
            var revision = root.GetProperty("backend_revision").GetString()
                ?? throw new InvalidDataException("backend_revision がありません。");
            var result = new List<ResolverCheckpoint>();
            foreach (var entry in root.GetProperty("files").EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString()!;
                var file = entry.GetProperty("file").GetString()!;
                if (System.IO.Path.GetFileName(file) != file)
                    throw new InvalidDataException($"重みのファイル名にパスは使えません: {file}");
                var path = System.IO.Path.Combine(directory, file);
                var exists = File.Exists(path);
                result.Add(new ResolverCheckpoint(name, file, entry.GetProperty("sha256").GetString()!,
                    exists ? await HashAsync(path, cancellationToken) : null, exists, revision));
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("歌唱の重みの記録ファイルが不正です: " + error.Message, error);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
