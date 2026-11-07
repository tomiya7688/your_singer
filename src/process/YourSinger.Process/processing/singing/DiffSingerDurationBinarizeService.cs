using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

// {
//   責務: [DiffSingerDurationBinarizeService: Duration入力から設定を作り、公式variance binarizationを実行して設定のパスを返す]
//   フィールド: [workerClient: 公式binarizationを実行するML worker]
// }
public sealed class DiffSingerDurationBinarizeService(MlWorkerClient workerClient)
{
    // {
    //   責務: [BinarizeAsync: 歌唱jobのDuration学習設定を作成し公式variance binarizationを実行する]
    //   引数: [workspace: 作業領域, job: Duration入力を生成済みの歌唱job, trainerRoot: 同梱DiffSingerのフォルダ]
    //   戻り値: [configPath: Duration学習で使う設定ファイルのパス(job領域内)]
    //   エラー: [歌唱job以外、Duration入力なし、設定のパスがjob領域外、worker異常で失敗する]
    //   補足: [Duration trainerの起動(#96)は含まない]
    // }
    public async Task<string> BinarizeAsync(ProjectWorkspace workspace, TrainingJobRecord job, string trainerRoot,
        CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Singing)
            throw new InvalidOperationException("歌唱jobのみDurationをbinarizationできます。");
        var jobId = TrainingInputDirectory.RequireComponent(job.JobId);
        var jobDirectory = Path.GetFullPath(Path.Combine(workspace.RootPath, "training", "diffsinger", jobId));
        if (!File.Exists(Path.Combine(jobDirectory, "duration-input", "transcriptions.csv")))
            throw new FileNotFoundException("Duration入力を先に生成してください。");
        var result = await workerClient.SendAsync("binarize_diffsinger_duration", new
        {
            trainer_root = trainerRoot,
            project_root = workspace.RootPath,
            job_id = jobId,
            speaker_id = job.SpeakerId
        }, cancellationToken);
        if (!result.TryGetProperty("config_path", out var config) || config.GetString() is not { } path ||
            !Path.GetFullPath(path).StartsWith(jobDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            throw new InvalidDataException("Duration学習設定の保存先がjob領域内にありません。");
        return Path.GetFullPath(path);
    }
}
