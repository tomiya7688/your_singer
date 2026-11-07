using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [SpeakerInventoryProvider: 保存済みのプロジェクトデータから、jobの話者の棚卸しを作る]
//   フィールド: [autoCorrection: 補正設定どおりのデータ表示を作る, classifications: 内容分類の保存先]
//   処理: [1: jobの補正設定でデータを組み立てる 2: 内容分類の記録を読む 3: 棚卸しサービスで集計する]
// }
public sealed class SpeakerInventoryProvider(AutoCorrectionService autoCorrection, ContentClassificationRepository classifications)
{
    private readonly SpeakerDataInventoryService _inventory = new();

    // {
    //   責務: [BuildAsync: 学習jobと同じ素材・補正設定で話者の棚卸しを作る]
    //   引数: [workspace: 作業領域, job: 対象のjob(話者・補正設定・素材のfingerprintを使う)]
    //   戻り値: [inventory: Preflightに渡す棚卸し]
    //   エラー: [内容分類が未保存のとき InvalidOperationException(0件の棚卸しで通さない)]
    //   補足: [区間の長さはWAVヘッダから読む。読めない区間はnull(0秒と読み替えない)で、測定状況に残る]
    // }
    public async Task<SpeakerDataInventory> BuildAsync(ProjectWorkspace workspace, TrainingJobRecord job,
        CancellationToken cancellationToken = default)
    {
        var classification = await classifications.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("内容分類がありません。先に音声の分類を実行してください。");
        var view = await autoCorrection.BuildAsync(workspace, job.AutoCorrectionEnabled, cancellationToken);
        return _inventory.Build(view.Segments, new SpeakerDataInventoryInput
        {
            SpeakerId = job.SpeakerId,
            DatasetFingerprint = job.DatasetFingerprint,
            Classification = classification,
            DurationSeconds = segment => WavDurationReader.Read(workspace, segment)
        });
    }
}
