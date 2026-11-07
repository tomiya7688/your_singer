using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

// {
//   責務: [DiffSingerBinarizeService: 音響学習入力から学習設定を作り、公式binarizationを実行して学習設定のパスを返す]
//   フィールド: [workerClient: 公式binarizationを実行するML worker]
// }
public sealed class DiffSingerBinarizeService(MlWorkerClient workerClient)
{
    // {
    //   責務: [BinarizeAsync: 歌唱jobの学習設定を作成し公式binarizationを実行する]
    //   引数: [workspace: 作業領域, job: 音響学習入力を生成済みの歌唱job, trainerRoot: 同梱DiffSingerのフォルダ]
    //   戻り値: [configPath: 学習で使う設定ファイルのパス(job領域内)]
    //   エラー: [歌唱job以外、音響学習入力なし、設定のパスがjob領域外、worker異常で失敗する]
    // }
    public async Task<string> BinarizeAsync(ProjectWorkspace workspace, TrainingJobRecord job, string trainerRoot,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱jobのみbinarizationできます。");
        var jobId = TrainingInputDirectory.RequireComponent(job.JobId);
        var jobDirectory = Path.GetFullPath(Path.Combine(workspace.RootPath, "training", "diffsinger", jobId));
        if (!File.Exists(Path.Combine(jobDirectory, "acoustic-input", "transcriptions.csv")))
            throw new FileNotFoundException("音響学習入力を先に生成してください。");
        var result = await workerClient.SendAsync("binarize_diffsinger", new
        {
            trainer_root = trainerRoot,
            project_root = workspace.RootPath,
            job_id = jobId,
            speaker_id = job.SpeakerId
        }, cancellationToken);
        if (!result.TryGetProperty("config_path", out var config) || config.GetString() is not { } path ||
            !Path.GetFullPath(path).StartsWith(jobDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            throw new InvalidDataException("学習設定の保存先がjob領域内にありません。");
        return Path.GetFullPath(path);
    }
}
