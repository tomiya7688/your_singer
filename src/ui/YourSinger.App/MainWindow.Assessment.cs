using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.App;

public sealed partial class MainWindow
{
    private const int AssessmentLineLimit = 200;
    private readonly TrainingAssessmentRepository _assessmentRepository = new();

    // {
    //   責務: [RefreshAssessmentAsync: 保存済みの学習用区間評価を、用途別の件数と区間ごとの説明として品質タブに出す]
    //   処理: [1: 全バッチの全jobの評価を読む 2: jobごとに件数を日本語1行にする 3: 区間の説明を上限件数まで並べる]
    //   副作用: [AssessmentSummaryText/AssessmentListを更新する。読めない評価は理由付きの行にし、元ファイルは変更しない]
    // }
    private async Task RefreshAssessmentAsync()
    {
        if (_workspace is null)
            return;
        var lines = new List<string>();
        var summaries = new List<string>();
        var history = await _trainingJobRepository.ListBatchesAsync(_workspace);
        foreach (var job in history.Batches.SelectMany(x => x.Jobs))
        {
            try
            {
                var record = await _assessmentRepository.LoadAsync(_workspace, job.JobId);
                if (record is null)
                    continue;
                summaries.Add(TrainingAssessmentPresenter.Summarize(TrainingAssessmentPresenter.Count(record)));
                lines.AddRange(record.Items.Select(TrainingAssessmentPresenter.Describe));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
            {
                lines.Add($"読み込めない評価があります(ジョブ {job.JobId}): 元ファイルは変更していません。");
            }
        }
        AssessmentSummaryText.Text = summaries.Count == 0
            ? "学習ジョブを作ると、採用・低品質採用・要確認・除外の件数が出ます。"
            : string.Join(Environment.NewLine, summaries);
        AssessmentList.ItemsSource = lines.Count > AssessmentLineLimit
            ? lines.Take(AssessmentLineLimit).Append($"…ほか {lines.Count - AssessmentLineLimit} 件").ToArray()
            : lines.ToArray();
    }
}
