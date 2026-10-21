using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

// {
//   責務: [DiffSingerAcousticInputService: 歌唱jobを公式音響学習のCSVと音声へ変換する]
//   フィールド: [workerClient: 音声切り出しを実行するML worker]
//   処理: [1: jobを検証する 2: 入力変換を要求する 3: 保存先を照合する]
// }
public sealed class DiffSingerAcousticInputService(MlWorkerClient workerClient)
{
    // {
    //   責務: [PrepareAsync: 実測音素境界から音響学習入力を生成する]
    //   引数: [workspace: 作業領域, job: Dataset Builderへ渡した歌唱job]
    //   戻り値: [inputDirectory: CSV・切り出し音声・変換記録を含む領域]
    //   エラー: [旧入力、job不一致、領域外、worker異常時に失敗する]
    // }
    public async Task<string> PrepareAsync(ProjectWorkspace workspace, TrainingJobRecord job,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱jobのみ音響学習入力を生成できます。");
        var jobId = TrainingInputDirectory.RequireComponent(job.JobId);
        var datasetDirectory = Path.Combine(workspace.RootPath, "training", "diffsinger", jobId);
        if (!File.Exists(Path.Combine(datasetDirectory, "dataset.json")))
            throw new FileNotFoundException("歌唱学習入力を先に生成してください。");

        // worker側でも保存済み入力のfingerprintと補正設定を照合する。
        var result = await workerClient.SendAsync("prepare_diffsinger_acoustic", new
        {
            project_root = workspace.RootPath, job_id = jobId, speaker_id = job.SpeakerId,
            dataset_fingerprint = job.DatasetFingerprint, auto_correction_enabled = job.AutoCorrectionEnabled
        }, cancellationToken);
        var expectedDirectory = Path.GetFullPath(Path.Combine(datasetDirectory, "acoustic-input"));
        if (!result.TryGetProperty("raw_data_dir", out var directory) ||
            directory.GetString() is not { } directoryPath ||
            !string.Equals(Path.GetFullPath(directoryPath), expectedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("音響学習入力の保存先が要求と一致しません。");
        if (!File.Exists(Path.Combine(expectedDirectory, "transcriptions.csv")) ||
            !File.Exists(Path.Combine(expectedDirectory, "provenance.json")))
            throw new InvalidDataException("音響学習入力に必要なファイルがありません。");
        return expectedDirectory;
    }
}
