using System.Text.RegularExpressions;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [TrainingModelNaming: トーク学習のモデル名を話者IDとjob IDから決める]
//   補足: [モデル名はTrainerのフォルダ名に使われるため、英数字・ハイフン・アンダースコアだけにし、job IDの先頭を付けて別jobと衝突させない]
// }
public static partial class TrainingModelNaming
{
    // {
    //   責務: [ModelName: 話者IDとjob IDから安全なモデル名を作る]
    //   引数: [speakerId: 話者ID, jobId: 学習job ID]
    //   戻り値: [name: "<話者>-<job先頭8文字>"。使えない文字は _ に置き換え、全体は64文字以内]
    //   エラー: [話者IDまたはjob IDが空のとき ArgumentException]
    // }
    public static string ModelName(string speakerId, string jobId)
    {
        if (string.IsNullOrWhiteSpace(speakerId) || string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("話者IDとjob IDが必要です。");
        var speaker = Unsafe().Replace(speakerId, "_");
        var job = Unsafe().Replace(jobId, "_");
        var suffix = job.Length > 8 ? job[..8] : job;
        var head = speaker.Length > 55 ? speaker[..55] : speaker;
        return $"{head}-{suffix}";
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex Unsafe();
}
