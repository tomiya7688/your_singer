using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

// {
//   責務: [DiffSingerDurationInputService: 生成済みの音響学習入力から、公式variance binarizer用のDuration入力を作る]
//   フィールド: [workerClient: Duration入力を生成するML worker]
//   処理: [1: jobと音響学習入力の存在を確認する 2: workerへ要求する 3: 保存先と必須ファイルを照合する]
// }
public sealed class DiffSingerDurationInputService(MlWorkerClient workerClient)
{
    // {
    //   責務: [PrepareAsync: 音響学習入力の実測音素列からDuration入力を生成する]
    //   引数: [workspace: 作業領域, job: 音響学習入力を生成済みの歌唱job]
    //   戻り値: [inputDirectory: transcriptions.csv・wavs・provenance.jsonを含む領域]
    //   エラー: [歌唱job以外、音響学習入力なし、保存先不一致、必須ファイルなし、worker異常で失敗する。使えるクリップが0件でも失敗する]
    //   補足: [使えないクリップは破棄せずprovenance.jsonのheldに理由付きで残る]
    // }
    public async Task<string> PrepareAsync(ProjectWorkspace workspace, TrainingJobRecord job,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱jobのみDuration入力を生成できます。");
        var jobId = TrainingInputDirectory.RequireComponent(job.JobId);
        var datasetDirectory = Path.Combine(workspace.RootPath, "training", "diffsinger", jobId);
        if (!File.Exists(Path.Combine(datasetDirectory, "acoustic-input", "provenance.json")))
            throw new FileNotFoundException("音響学習入力を先に生成してください。");
        var result = await workerClient.SendAsync("prepare_diffsinger_duration", new
        {
            project_root = workspace.RootPath,
            job_id = jobId,
            dataset_fingerprint = job.DatasetFingerprint
        }, cancellationToken);
        var expectedDirectory = Path.GetFullPath(Path.Combine(datasetDirectory, "duration-input"));
        if (!result.TryGetProperty("raw_data_dir", out var directory) ||
            directory.GetString() is not { } directoryPath ||
            !string.Equals(Path.GetFullPath(directoryPath), expectedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Duration入力の保存先が要求と一致しません。");
        if (!File.Exists(Path.Combine(expectedDirectory, "transcriptions.csv")) ||
            !File.Exists(Path.Combine(expectedDirectory, "provenance.json")))
            throw new InvalidDataException("Duration入力に必要なファイルがありません。");
        return expectedDirectory;
    }
}
