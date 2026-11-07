using System.Security.Cryptography;
using System.Text.Json;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [TalkCheckpointInventory: 同梱のトーク事前学習重みを、固定記録(lock)の期待hashと実ファイルのhashで棚卸しする]
//   処理: [1: lock の pretrained 資源から期待sha256と固定版を読む 2: 同梱フォルダの実ファイルを探す 3: 存在すればsha256を計算する]
//   補足: [期待hashはlockから、実測hashは実ファイルから取る。lockが読めない・資源が無いときは例外にし、空の棚卸しで通さない]
// }
public static class TalkCheckpointInventory
{
    public static readonly string[] Names = ["G_0.safetensors", "D_0.safetensors", "DUR_0.safetensors"];

    // {
    //   責務: [ScanAsync: Preflightに渡す初期重みの棚卸しを作る]
    //   引数: [trainerRoot: 同梱Style-Bert-VITS2のフォルダ(pretrained/を含む), lockPath: style-bert-vits2-training.lock.json]
    //   戻り値: [checkpoints: 3つの重みごとの ResolverCheckpoint(欠落は Exists=false、実測hashなし)]
    //   エラー: [lockが無い・不正、pretrained資源や期待hashが無いとき InvalidDataException / FileNotFoundException]
    // }
    public static async Task<IReadOnlyList<ResolverCheckpoint>> ScanAsync(string trainerRoot, string lockPath,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(lockPath, cancellationToken));
        var root = document.RootElement;
        var revision = root.TryGetProperty("source_revision", out var rev) ? rev.GetString() : null;
        if (string.IsNullOrEmpty(revision) || !root.TryGetProperty("resources", out var resources))
            throw new InvalidDataException("固定記録に版または資源がありません。");
        var pretrained = resources.EnumerateArray().FirstOrDefault(x => x.GetProperty("name").GetString() == "pretrained");
        if (pretrained.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("固定記録に pretrained 資源がありません。");
        var expected = pretrained.GetProperty("files").EnumerateArray()
            .ToDictionary(x => x.GetProperty("path").GetString()!, x => x.GetProperty("sha256").GetString()!);
        var result = new List<ResolverCheckpoint>();
        foreach (var name in Names)
        {
            if (!expected.TryGetValue(name, out var expectedHash))
                throw new InvalidDataException($"固定記録に {name} のhashがありません。");
            var path = System.IO.Path.Combine(trainerRoot, "pretrained", name);
            var exists = File.Exists(path);
            result.Add(new ResolverCheckpoint(name, "pretrained/" + name, expectedHash,
                exists ? await HashAsync(path, cancellationToken) : null, exists, revision));
        }
        return result;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
