using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Dataset;

// {
//   責務: [
//     UniversalVoiceDatasetSegmentReader: ワーカー結果をsegmentへ変換しキャッシュ素材を検証する
//   ]
//   処理: [
//     1: ワーカーのJSON結果をsegmentへ変換する
//     2: segmentが参照する特徴ファイルの存在を確認する
//   ]
//   フィールド: []
// }
internal static class UniversalVoiceDatasetSegmentReader
{
    // {
    //   責務: [ParseWorkerResult: ワーカーのJSON結果をUniversalVoiceSegmentへ変換する]
    //   処理: [1: snake_caseと列挙型を内部モデルへ変換する]
    //   引数: [result: ワーカーから受け取ったJSON]
    //   戻り値: [segment: 変換後の共通segment]
    //   エラー: [結果を読み取れない場合にInvalidDataExceptionを送出する]
    // }
    internal static UniversalVoiceSegment ParseWorkerResult(JsonElement result)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        return JsonSerializer.Deserialize<UniversalVoiceSegment>(
            result.GetRawText(),
            options) ?? throw new InvalidDataException(
                "共通特徴抽出結果を読み取れませんでした。");
    }

    // {
    //   責務: [HasFeatureFiles: segmentが参照する音響特徴ファイルの存在を確認する]
    //   処理: [1: F0・energy・voicingの相対パスをworkspace内で検査する]
    //   引数: [workspace: 作業領域, segment: 参照先を検証するsegment]
    //   戻り値: [exists: 3種類の特徴ファイルがすべて存在する場合はtrue]
    // }
    internal static bool HasFeatureFiles(
        ProjectWorkspace workspace,
        UniversalVoiceSegment segment) =>
        Exists(workspace, segment.F0FeaturePath) &&
        Exists(workspace, segment.EnergyFeaturePath) &&
        Exists(workspace, segment.VoicingFeaturePath);

    // {
    //   責務: [Exists: 相対特徴パスがworkspace内の既存ファイルを指すか調べる]
    //   処理: [1: 空パスを除外しworkspaceを基準にファイルの存在を確認する]
    //   引数: [workspace: 作業領域, path: workspaceからの相対パス]
    //   戻り値: [exists: ファイルが存在する場合はtrue]
    // }
    private static bool Exists(ProjectWorkspace workspace, string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(workspace.RootPath, path));
}
