using System.Diagnostics;
using Avalonia.Interactivity;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.App;

public sealed partial class TrainingHistoryWindow
{
    // {
    //   責務: [SampleRow: 試聴ボックスの1項目(ジョブ名つきの試験音声)]
    //   フィールド: [JobId: 対象ジョブ, Sample: 試験音声]
    // }
    private sealed record SampleRow(string JobId, VerificationSample Sample)
    {
        public override string ToString() => $"{JobId} / {Sample.Label}" + (TrainingVerificationPresenter.IsPlayable(Sample) ? "" : "(再生不可)");
    }

    // {
    //   責務: [DescribeVerification: ジョブ一式の検証状態の行を作り、試聴の選択肢を更新する]
    //   処理: [各ジョブの verification.json を読み、工程別の状態と試験音声を表示用の行にする]
    //   引数: [batch: 選択中のジョブ一式]
    //   戻り値: [text: 詳細欄へ足す日本語テキスト]
    //   副作用: [VerificationSampleCombo の項目を置き換える]
    // }
    private string DescribeVerification(TrainingJobBatch batch)
    {
        var rows = new List<SampleRow>();
        var text = new List<string>();
        foreach (var job in batch.Jobs)
        {
            TrainingVerificationLoad load = _workspace is null
                ? new(VerificationLoadStatus.Missing, null, null, "")
                : TrainingVerificationStore.Load(_workspace, job.JobId);
            var samples = TrainingVerificationStore.Samples(load);
            rows.AddRange(samples.Select(s => new SampleRow(job.JobId, s)));
            text.Add($"【{job.JobId} の学習後の検証】");
            text.AddRange(TrainingVerificationPresenter.Describe(job.State, load, samples));
        }
        VerificationSampleCombo.ItemsSource = rows;
        VerificationSampleCombo.SelectedItem = rows.FirstOrDefault(r => TrainingVerificationPresenter.IsPlayable(r.Sample));
        OpenSampleButton.IsEnabled = VerificationSampleCombo.SelectedItem is SampleRow;
        return string.Join("\n", text);
    }

    private void OnSampleSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e) =>
        OpenSampleButton.IsEnabled = VerificationSampleCombo.SelectedItem is SampleRow row
            && TrainingVerificationPresenter.IsPlayable(row.Sample);

    private void OnOpenSampleClick(object? sender, RoutedEventArgs e)
    {
        if (VerificationSampleCombo.SelectedItem is not SampleRow row || !TrainingVerificationPresenter.IsPlayable(row.Sample)) return;
        try
        {
            // 既定のアプリで再生する(音声再生ライブラリは使わない)。
            System.Diagnostics.Process.Start(new ProcessStartInfo(row.Sample.FullPath) { UseShellExecute = true });
            HistoryStatusText.Text = $"試験音声を開きました: {row.Sample.Label}";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            HistoryStatusText.Text = $"試験音声を開けませんでした。ファイルは次の場所にあります。\n{row.Sample.FullPath}";
        }
    }
}
