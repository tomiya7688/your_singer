using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// DiffSinger の学習入力が、評価の eligible な区間だけを区間Weight manifestと1対1で持つことを確認する。
// 公式binarization後の対応(sampler)は後続(#92)。ここでは入力の対応と改竄検出まで。
public sealed class DiffSingerWeightManifestTests
{
    private static readonly SpeakerTrainingSelection Singing = new()
    {
        SpeakerId = "spk_a",
        Target = TrainingTarget.Singing,
        AutoCorrectionEnabled = false
    };

    [Fact]
    public async Task OnlyEligibleSegmentsEnterTheInputAndTheManifest_TheRestIsKeptAsSkipped()
    {
        using var p = new TestProject();
        var silent = p.Segment("silent", type: SegmentContentType.Singing);
        silent.VoicedRatio = 0;
        await p.SeedAsync(p.Segment("keep-a", type: SegmentContentType.Singing), p.Segment("keep-b", type: SegmentContentType.Singing), silent);
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Singing], TestProject.Token)).Jobs);
        var record = await new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, TestProject.Token);

        Assert.Equal(["keep-a", "keep-b"], record.Items.Select(x => x.SegmentId).Order(StringComparer.Ordinal));
        Assert.Equal(TrainingWeightManifest.FileName, record.WeightManifestFile);
        var root = Path.Combine(p.Workspace.RootPath, "training", "diffsinger", job.JobId);
        var manifest = await TrainingWeightManifestService.VerifyAsync(
            Path.Combine(root, TrainingWeightManifest.FileName), record.WeightManifestSha256!,
            ["keep-a.wav", "keep-b.wav"], TestProject.Token);
        Assert.Equal(["keep-a", "keep-b"], manifest.Items.Select(x => x.SegmentId));
        Assert.Contains(manifest.Skipped, x => x.SegmentId == "silent" && x.Eligibility == "excluded");
        Assert.All(manifest.Items, x => Assert.InRange(x.TrainingWeight, TrainingWeightPolicy.MinWeight, TrainingWeightPolicy.MaxWeight));
    }

    [Fact]
    public async Task ATamperedManifestIsDetectedByItsRecordedHash()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a", type: SegmentContentType.Singing));
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Singing], TestProject.Token)).Jobs);
        var record = await new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, TestProject.Token);
        var path = Path.Combine(p.Workspace.RootPath, "training", "diffsinger", job.JobId, TrainingWeightManifest.FileName);
        await File.AppendAllTextAsync(path, " ", TestProject.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TrainingWeightManifestService.VerifyAsync(path, record.WeightManifestSha256!, ["a.wav"], TestProject.Token));
    }

    [Fact]
    public async Task LegacyJobsWithoutAnAssessmentAreRejectedBeforeAnythingIsPublished()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a", type: SegmentContentType.Singing));
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Singing], TestProject.Token)).Jobs);
        job.InputFingerprint = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, TestProject.Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.RootPath, "training", "diffsinger")));
    }
}
