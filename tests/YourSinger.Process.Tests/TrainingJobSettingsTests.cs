using System.Text.Json;
using System.Text.Json.Nodes;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// ジョブへ保存する目的・質感・学習モード・初期化情報の契約を確認する(実Trainerの起動は対象外)。
public sealed class TrainingJobSettingsTests
{
    private static CancellationToken Token => TestProject.Token;
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static TrainingTextureRequest Texture(string preset, params string[] refs) =>
        new() { Preset = preset, ReferenceSegmentIds = [.. refs] };

    private static TrainingInitializationComponent Checkpoint() =>
        new() { Name = "acoustic", Sha256 = Hash, Path = "pretrained/acoustic.ckpt" };

    private static async Task<TestProject> SeedAsync()
    {
        var p = new TestProject();
        await p.SeedAsync(p.Segment("talk1"), p.Segment("song1", "spk_a", SegmentContentType.Singing));
        return p;
    }

    private static SpeakerTrainingSelection Both(TrainingJobOptions? talk, TrainingJobOptions? singing) => new()
    {
        SpeakerId = "spk_a", Target = TrainingTarget.Both, AutoCorrectionEnabled = false,
        TalkOptions = talk, SingingOptions = singing
    };

    [Fact]
    public async Task Serialization_RoundTripsModes()
    {
        using var p = await SeedAsync();
        var batch = await p.Planner().CreateBatchAsync(p.Workspace, [Both(
            new() { Mode = TrainingMode.FromScratch, Texture = Texture("natural"), Seed = 7 },
            new() { Mode = TrainingMode.FineTune, Texture = Texture("breathy", "song1"), BaseCheckpoints = [Checkpoint()] })], Token);
        var loaded = (await p.Jobs.LoadBatchAsync(p.Workspace, batch.BatchId, Token))!;
        var talk = loaded.Jobs.Single(x => x.Target == TrainingTarget.Talk);
        var singing = loaded.Jobs.Single(x => x.Target == TrainingTarget.Singing);
        Assert.Equal(TrainingMode.FromScratch, talk.ResolvedTrainingMode);
        Assert.Equal(TrainingJobVocabulary.RandomInitialization, talk.Initialization!.Type);
        Assert.Empty(talk.Initialization.Components);
        Assert.Equal(7, talk.Seed);
        Assert.Equal(TrainingMode.FineTune, singing.RequestedTrainingMode);
        Assert.Equal("acoustic", singing.Initialization!.Components.Single().Name);
        Assert.Equal(["song1"], singing.RequestedTexture!.ReferenceSegmentIds);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(p.Workspace.MetadataPath, "training-jobs", batch.BatchId + ".json"), Token))!;
        var modes = json["jobs"]!.AsArray().Select(x => (string)x!["requested_training_mode"]!).Order().ToArray();
        Assert.Equal(new[] { "fine_tune", "from_scratch" }, modes);
    }

    [Fact]
    public async Task AutoStaysUnresolvedUntilPlanIsResolved()
    {
        using var p = await SeedAsync();
        var batch = await p.Planner().CreateBatchAsync(p.Workspace, [Both(null, null)], Token);
        Assert.All(batch.Jobs, job =>
        {
            Assert.Equal(TrainingMode.Auto, job.RequestedTrainingMode);
            Assert.Null(job.ResolvedTrainingMode);
            Assert.Null(job.Initialization);
            Assert.Equal(TrainingJobVocabulary.DefaultTexturePreset, job.RequestedTexture!.Preset);
        });
    }

    [Fact]
    public async Task LegacyHistory_NotPassedOffAsNew()
    {
        using var p = await SeedAsync();
        var batch = await p.Planner().CreateBatchAsync(p.Workspace, [Both(null, null)], Token);
        var path = Path.Combine(p.Workspace.MetadataPath, "training-jobs", batch.BatchId + ".json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
        node.Remove("input_version");
        foreach (var job in node["jobs"]!.AsArray())
            foreach (var key in new[] { "requested_texture", "requested_training_mode", "resolved_training_mode", "initialization", "seed" })
                job!.AsObject().Remove(key);
        await File.WriteAllTextAsync(path, node.ToJsonString(), Token);
        var legacy = (await p.Jobs.LoadBatchAsync(p.Workspace, batch.BatchId, Token))!;
        Assert.All(legacy.Jobs, job => Assert.True(job.IsLegacyWithoutTrainingContract));
        Assert.True(TrainingJobPlanningService.RequiresRecreation(legacy));
        Assert.Throws<InvalidOperationException>(() => TrainingJobContractFingerprint.Compute(legacy.Jobs[0]));
    }

    [Fact]
    public async Task InvalidEnum_Rejected()
    {
        using var p = await SeedAsync();
        var batch = await p.Planner().CreateBatchAsync(p.Workspace, [Both(null, null)], Token);
        var path = Path.Combine(p.Workspace.MetadataPath, "training-jobs", batch.BatchId + ".json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!.AsObject();
        node["jobs"]![0]!["requested_training_mode"] = "turbo";
        await File.WriteAllTextAsync(path, node.ToJsonString(), Token);
        await Assert.ThrowsAsync<JsonException>(() => p.Jobs.LoadBatchAsync(p.Workspace, batch.BatchId, Token));
        var history = await p.Jobs.ListBatchesAsync(p.Workspace, Token);
        Assert.Single(history.Errors);
    }

    [Fact]
    public async Task Both_ExpandsWithIndependentTextureAndMode()
    {
        using var p = await SeedAsync();
        var batch = await p.Planner().CreateBatchAsync(p.Workspace, [Both(
            new() { Texture = Texture("singing_like") }, new() { Texture = Texture("natural"), Mode = TrainingMode.FromScratch })], Token);
        var talk = batch.Jobs.Single(x => x.Target == TrainingTarget.Talk);
        var singing = batch.Jobs.Single(x => x.Target == TrainingTarget.Singing);
        Assert.Equal("singing_like", talk.RequestedTexture!.Preset);
        Assert.Equal(TrainingMode.Auto, talk.RequestedTrainingMode);
        Assert.Equal("natural", singing.RequestedTexture!.Preset);
        Assert.Equal(TrainingMode.FromScratch, singing.RequestedTrainingMode);
    }

    [Fact]
    public async Task TextureChange_ChangesContractFingerprintButNotDatasetFingerprint()
    {
        using var p = await SeedAsync();
        var a = (await p.Planner().CreateBatchAsync(p.Workspace, [Both(new() { Texture = Texture("natural") }, null)], Token))
            .Jobs.Single(x => x.Target == TrainingTarget.Talk);
        var b = (await p.Planner().CreateBatchAsync(p.Workspace, [Both(new() { Texture = Texture("bright") }, null)], Token))
            .Jobs.Single(x => x.Target == TrainingTarget.Talk);
        Assert.Equal(a.DatasetFingerprint, b.DatasetFingerprint);
        Assert.NotEqual(TrainingJobContractFingerprint.Compute(a), TrainingJobContractFingerprint.Compute(b));
        Assert.Equal(TrainingJobContractFingerprint.Compute(a), TrainingJobContractFingerprint.Compute(a));
    }

    [Fact]
    public async Task Recreate_KeepsSettings()
    {
        using var p = await SeedAsync();
        var source = await p.Planner().CreateBatchAsync(p.Workspace, [Both(
            new() { Mode = TrainingMode.FromScratch, Texture = Texture("bright"), Seed = 3 },
            new() { Mode = TrainingMode.FineTune, BaseCheckpoints = [Checkpoint()] })], Token);
        var recreated = await p.Planner().RecreateBatchAsync(p.Workspace, source.BatchId, Token);
        foreach (var target in new[] { TrainingTarget.Talk, TrainingTarget.Singing })
        {
            var before = source.Jobs.Single(x => x.Target == target);
            var after = recreated.Jobs.Single(x => x.Target == target);
            Assert.Equal(before.RequestedTrainingMode, after.RequestedTrainingMode);
            Assert.Equal(before.RequestedTexture!.Preset, after.RequestedTexture!.Preset);
            Assert.Equal(before.Seed, after.Seed);
            Assert.Equal(TrainingJobContractFingerprint.Compute(before).Length, TrainingJobContractFingerprint.Compute(after).Length);
            Assert.Equal(before.Initialization?.Components.Count, after.Initialization?.Components.Count);
        }
    }

    [Fact]
    public async Task FromScratchWithBaseCheckpoint_Rejected()
    {
        using var p = await SeedAsync();
        var bad = Both(new() { Mode = TrainingMode.FromScratch, BaseCheckpoints = [Checkpoint()] }, null);
        await Assert.ThrowsAsync<ArgumentException>(() => p.Planner().CreateBatchAsync(p.Workspace, [bad], Token));
        Assert.Empty((await p.Jobs.ListBatchesAsync(p.Workspace, Token)).Batches);
    }

    [Theory]
    [InlineData("fine_tune_without_checkpoint")]
    [InlineData("auto_with_checkpoint")]
    [InlineData("bad_hash")]
    [InlineData("absolute_path")]
    [InlineData("parent_path")]
    [InlineData("empty_preset")]
    [InlineData("undefined_mode")]
    public async Task InvalidOptions_Rejected(string kind)
    {
        using var p = await SeedAsync();
        var options = kind switch
        {
            "fine_tune_without_checkpoint" => new TrainingJobOptions { Mode = TrainingMode.FineTune },
            "auto_with_checkpoint" => new TrainingJobOptions { BaseCheckpoints = [Checkpoint()] },
            "bad_hash" => new TrainingJobOptions
            {
                Mode = TrainingMode.FineTune, BaseCheckpoints = [new() { Name = "a", Sha256 = "xyz", Path = "a.ckpt" }]
            },
            "absolute_path" => new TrainingJobOptions
            {
                Mode = TrainingMode.FineTune, BaseCheckpoints = [new() { Name = "a", Sha256 = Hash, Path = "C:/x/a.ckpt" }]
            },
            "parent_path" => new TrainingJobOptions
            {
                Mode = TrainingMode.FineTune, BaseCheckpoints = [new() { Name = "a", Sha256 = Hash, Path = "../a.ckpt" }]
            },
            "empty_preset" => new TrainingJobOptions { Texture = new TrainingTextureRequest { Preset = " " } },
            _ => new TrainingJobOptions { Mode = (TrainingMode)99 }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => p.Planner().CreateBatchAsync(p.Workspace, [Both(options, null)], Token));
    }

    [Fact]
    public void ContractFingerprint_IsOrderIndependentForReferences()
    {
        TrainingJobRecord Job(params string[] refs) => new()
        {
            JobId = "j", SpeakerId = "s", Target = TrainingTarget.Talk, DatasetFingerprint = "fp",
            RequestedTexture = Texture("natural", refs), RequestedTrainingMode = TrainingMode.Auto
        };
        Assert.Equal(TrainingJobContractFingerprint.Compute(Job("a", "b")), TrainingJobContractFingerprint.Compute(Job("b", "a")));
    }
}
