using System.Text.Json.Nodes;
using YourSinger.Data.Models;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Talk;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// SBV2の学習入力に、公式esd.listを変えずに区間Weight manifestを出力することを確認する(samplerへの適用は対象外)。
public sealed class StyleBertVits2WeightManifestTests
{
    private static CancellationToken Token => TestProject.Token;

    private static async Task<TrainingJobRecord> CreateJobAsync(TestProject project) =>
        Assert.Single((await project.Planner().CreateBatchAsync(project.Workspace,
            [new SpeakerTrainingSelection { SpeakerId = "spk_a", Target = TrainingTarget.Talk, AutoCorrectionEnabled = false }],
            Token)).Jobs);

    private static string RootOf(StyleBertVits2DatasetRecord dataset) =>
        Path.GetDirectoryName(Path.GetDirectoryName(dataset.Items[0].AudioPath))!;

    [Fact]
    public async Task Manifest_SixSegmentWeights_MatchesEsdListOneToOne()
    {
        using var project = new TestProject();
        var segments = Enumerable.Range(0, 6)
            .Select(i => project.Segment($"s{i}", confidence: 0.3 + i * 0.12)).ToArray();
        await project.SeedAsync(segments);
        var job = await CreateJobAsync(project);
        var dataset = await new StyleBertVits2DatasetBuilder(project.DatasetRepository)
            .BuildAsync(project.Workspace, job, "話者", Token);
        var root = RootOf(dataset);
        var lines = await File.ReadAllLinesAsync(Path.Combine(root, "esd.list"), Token);
        Assert.Equal(6, lines.Length);
        Assert.All(lines, x => Assert.Equal(4, x.Split('|').Length));

        var names = lines.Select(x => x.Split('|')[0]).ToArray();
        var manifest = await TrainingWeightManifestService.VerifyAsync(
            Path.Combine(root, dataset.WeightManifestFile!), dataset.WeightManifestSha256!, names, Token);
        Assert.Equal(6, manifest.Items.Count);
        Assert.All(manifest.Items, x => Assert.InRange(x.TrainingWeight, 0.25, 2.0));
        Assert.Equal(job.InputFingerprint, manifest.InputFingerprint);
        Assert.True(manifest.Items.Select(x => x.TrainingWeight).Distinct().Count() > 1);
    }

    [Fact]
    public async Task NonEligibleSegments_AreNotInEsdListAndAreRecordedAsSkipped()
    {
        using var project = new TestProject();
        var silent = project.Segment("silent");
        silent.VoicedRatio = 0;
        await project.SeedAsync(project.Segment("good"), silent);
        var job = await CreateJobAsync(project);
        var dataset = await new StyleBertVits2DatasetBuilder(project.DatasetRepository)
            .BuildAsync(project.Workspace, job, "話者", Token);
        var root = RootOf(dataset);
        Assert.Equal(["good.wav|話者|JP|あいうえお"], await File.ReadAllLinesAsync(Path.Combine(root, "esd.list"), Token));
        Assert.False(File.Exists(Path.Combine(root, "raw", "silent.wav")));
        var manifest = await TrainingWeightManifestService.VerifyAsync(
            Path.Combine(root, dataset.WeightManifestFile!), dataset.WeightManifestSha256!, ["good.wav"], Token);
        var skipped = Assert.Single(manifest.Skipped);
        Assert.Equal("silent", skipped.SegmentId);
        Assert.Equal(["no_usable_voice"], skipped.ReasonCodes);
    }

    [Fact]
    public async Task TamperedManifest_IsDetectedByHash()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("a"), project.Segment("b"));
        var dataset = await new StyleBertVits2DatasetBuilder(project.DatasetRepository)
            .BuildAsync(project.Workspace, await CreateJobAsync(project), "話者", Token);
        var path = Path.Combine(RootOf(dataset), dataset.WeightManifestFile!);
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path, Token)).Replace("training_weight", "training_weigh_"), Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => TrainingWeightManifestService.VerifyAsync(
            path, dataset.WeightManifestSha256!, ["a.wav", "b.wav"], Token));
    }

    [Fact]
    public async Task TamperedAssessment_IsRejectedBeforeAnythingIsPublished()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("a"), project.Segment("b"));
        var job = await CreateJobAsync(project);
        var path = Path.Combine(project.Workspace.TrainingPath, "assessments", job.JobId, "assessment.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
        node["items"]![0]!["training_weight"] = 1.2345;
        await File.WriteAllTextAsync(path, node.ToJsonString(), Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => new StyleBertVits2DatasetBuilder(project.DatasetRepository)
            .BuildAsync(project.Workspace, job, "話者", Token));
        TestProject.AssertNoTrainingInputPublished(project.Workspace);
    }

    [Fact]
    public async Task OldJob_RequiresRecreation()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("a"));
        var current = await CreateJobAsync(project);
        var old = new TrainingJobRecord
        {
            JobId = current.JobId, SpeakerId = current.SpeakerId, Target = TrainingTarget.Talk,
            DatasetFingerprint = current.DatasetFingerprint, SegmentIds = current.SegmentIds
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StyleBertVits2DatasetBuilder(project.DatasetRepository)
            .BuildAsync(project.Workspace, old, "話者", Token));
        TestProject.AssertNoTrainingInputPublished(project.Workspace);
    }

    [Fact]
    public async Task Canceled_NoPublish()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("a"));
        var job = await CreateJobAsync(project);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StyleBertVits2DatasetBuilder(project.DatasetRepository)
            .BuildAsync(project.Workspace, job, "話者", cts.Token));
        TestProject.AssertNoTrainingInputPublished(project.Workspace);
    }

    [Fact]
    public void MissingSegment_Rejected()
    {
        var item = new TrainingAssessmentItem
        {
            SegmentId = "a", Eligibility = "eligible", ReasonCodes = [], RawWeight = 1, TrainingWeight = 1,
            Quality = new() { Value = null, Status = ConfidenceStatus.Unavailable, Components = [] },
            Utility = new() { Value = null, Status = ConfidenceStatus.Unavailable, ReasonCodes = [] }
        };
        var assessment = new TrainingAssessmentRecord
        {
            SchemaVersion = "1", PolicyVersion = TrainingWeightPolicy.PolicyVersion, JobId = "j", SpeakerId = "s",
            Target = "talk", SourceDatasetFingerprint = "fp", Items = [item]
        };
        var job = new TrainingJobRecord
        {
            JobId = "j", SpeakerId = "s", Target = TrainingTarget.Talk, DatasetFingerprint = "fp",
            RequestedTexture = new TrainingTextureRequest { Preset = "natural" }, RequestedTrainingMode = TrainingMode.Auto,
            Backend = "b", BackendRevision = "r"
        };
        job.InputFingerprint = TrainingInputFingerprint.Compute(new TrainingInputFingerprintInput
        {
            DatasetFingerprint = "fp", ContractFingerprint = TrainingJobContractFingerprint.Compute(job),
            Backend = "b", BackendRevision = "r", Assessment = assessment
        });
        Assert.Throws<InvalidDataException>(() =>
            TrainingWeightManifestService.Build(job, assessment, ["a", "missing"], id => id + ".wav"));
        var (manifest, used) = TrainingWeightManifestService.Build(job, assessment, ["a"], id => id + ".wav");
        Assert.Equal(["a"], used);
        Assert.Equal("a.wav", Assert.Single(manifest.Items).AudioFilename);
    }

    private static TrainingWeightManifest Manifest(params TrainingWeightManifestItem[] items) => new()
    {
        SchemaVersion = "1", JobId = "j", PolicyVersion = "p", InputFingerprint = "f", Items = [.. items]
    };

    private static TrainingWeightManifestItem Entry(string id, string file, double weight = 1) =>
        new() { SegmentId = id, AudioFilename = file, TrainingWeight = weight };

    [Fact]
    public void Duplicate_Rejected()
    {
        Assert.Throws<InvalidDataException>(() => TrainingWeightManifestService.Validate(
            Manifest(Entry("a", "a.wav"), Entry("a", "b.wav")), ["a.wav", "b.wav"]));
        Assert.Throws<InvalidDataException>(() => TrainingWeightManifestService.Validate(
            Manifest(Entry("a", "x.wav"), Entry("b", "x.wav")), ["x.wav"]));
    }

    [Fact]
    public void PathTraversal_Rejected() =>
        Assert.Throws<InvalidDataException>(() => TrainingWeightManifestService.Validate(
            Manifest(Entry("a", "../a.wav")), ["../a.wav"]));

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.24)]
    [InlineData(2.01)]
    [InlineData(double.NaN)]
    public void OutOfRangeWeight_Rejected(double weight) =>
        Assert.Throws<InvalidDataException>(() => TrainingWeightManifestService.Validate(
            Manifest(Entry("a", "a.wav", weight)), ["a.wav"]));

    [Fact]
    public void MissingOrExtraAudio_Rejected()
    {
        var manifest = Manifest(Entry("a", "a.wav"), Entry("b", "b.wav"));
        Assert.Throws<InvalidDataException>(() => TrainingWeightManifestService.Validate(manifest, ["a.wav"]));
        Assert.Throws<InvalidDataException>(() =>
            TrainingWeightManifestService.Validate(manifest, ["a.wav", "b.wav", "c.wav"]));
    }
}
