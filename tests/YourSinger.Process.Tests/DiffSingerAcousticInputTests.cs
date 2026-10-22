using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Singing;

namespace YourSinger.Process.Tests;

// {
//   責務: [DiffSingerAcousticInputTests: 音素長だけでなく元音声の実測境界が保存されることを確認する]
//   処理: [1: 先頭余白がある合成区間を計画する 2: Builderの出力を読み直す]
// }
public sealed class DiffSingerAcousticInputTests
{
    // { 責務: [DatasetPreservesMeasuredPhoneOffsets: 先頭の余白を詰めずに開始・終了時刻を保存する] }
    [Fact]
    public async Task DatasetPreservesMeasuredPhoneOffsets()
    {
        using var project = new TestProject();
        var segment = project.Segment("offset", type: SegmentContentType.Singing);
        segment.Phonemes.Clear();
        segment.Phonemes.Add(new() { Phoneme = "a", StartSec = 0.2, EndSec = 0.7, Confidence = 0 });
        await project.SeedAsync(segment);
        var job = Assert.Single((await project.Planner().CreateBatchAsync(project.Workspace,
            [new SpeakerTrainingSelection
            {
                SpeakerId = "spk_a", Target = TrainingTarget.Singing, AutoCorrectionEnabled = false
            }], TestProject.Token)).Jobs);
        var dataset = await new DiffSingerDatasetBuilder(project.DatasetRepository)
            .BuildAsync(project.Workspace, job, TestProject.Token);
        var item = Assert.Single(dataset.Items);
        Assert.Equal([0.2], item.PhonemeStartsSec);
        Assert.Equal([0.7], item.PhonemeEndsSec);
        Assert.Equal(DiffSingerDatasetBuilder.RequiredAlignmentMethod, item.AlignmentMethod);
        var path = Path.Combine(project.Workspace.RootPath, "training", "diffsinger", job.JobId, "dataset.json");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestProject.Token));
        var saved = json.RootElement.GetProperty("items")[0];
        Assert.Equal(0.2, saved.GetProperty("phoneme_starts_sec")[0].GetDouble());
        Assert.Equal(0.7, saved.GetProperty("phoneme_ends_sec")[0].GetDouble());
    }
}
