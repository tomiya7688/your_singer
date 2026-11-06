using System.Security.Cryptography;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// worker の実行記録と計画の照合を確認する。初期重みの実読込の観測は worker 側(#93/#94)が未実装のため、未観測の扱いも確認する。
public sealed class TrainingRunReportVerifierTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "YourSinger.Tests", Guid.NewGuid().ToString("N"));

    public TrainingRunReportVerifierTests()
    {
        Directory.CreateDirectory(_output);
        File.WriteAllText(Path.Combine(_output, "m.safetensors"), "weights");
    }

    public void Dispose()
    {
        if (Directory.Exists(_output)) Directory.Delete(_output, true);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static TrainingJobRecord Job(string? mode = "fine_tune", int? seed = 7, params (string Name, string Sha)[] components) => new()
    {
        JobId = "job-1", SpeakerId = "spk_a", Target = TrainingTarget.Talk, DatasetFingerprint = "fp", Seed = seed,
        ResolvedPlan = new TrainingResolvedPlanRecord
        {
            ManifestHash = "h", InventoryRevision = "r", Executable = true, ResolvedTrainingMode = mode,
            Stages = mode is null ? [] :
            [
                new TrainingResolvedStageRecord
                {
                    StageId = "s", OperationId = "o", Mode = mode,
                    Initialization = mode == "fine_tune"
                        ? Pretrained(components)
                        : new TrainingInitialization { Type = TrainingJobVocabulary.RandomInitialization }
                }
            ]
        }
    };

    private static TrainingRunReport Report(string? mode = "fine_tune", int? seed = 7, int exit = 0, string? path = "m.safetensors",
        string? sha = null, TrainingInitialization? observed = null, string schema = "1", string jobId = "job-1",
        string? evidence = TrainingInitializationEvidence.Loaded, bool sampled = true) => new()
    {
        SchemaVersion = schema, JobId = jobId, ResolvedMode = mode, Seed = seed, TrainerExitCode = exit, InitializationObserved = observed, InitializationEvidence = observed is null ? null : evidence,
        WeightSampling = sampled ? new TrainingWeightSamplingRecord { Applied = true, SamplerVersion = "weighted-bucket-v1", Seed = seed, ItemCount = 1, Iterations = 1 } : null,
        Artifacts = path is null ? [] : [new TrainingRunArtifact { Kind = "model_weights", Path = path, Sha256 = sha ?? Hash("weights") }]
    };

    private static TrainingInitialization Pretrained(params (string Name, string Sha)[] components) => new()
    {
        Type = TrainingJobVocabulary.PretrainedCheckpoint,
        Components = [.. components.Select(x => new TrainingInitializationComponent { Name = x.Name, Sha256 = x.Sha, Path = "p" })]
    };

    [Fact]
    public void MatchingReportWithObservedInitialization_IsVerified()
    {
        var job = Job("fine_tune", 7, ("G", "aa"));
        var record = TrainingRunReportVerifier.Verify(job, Report(observed: Pretrained(("G", "AA"))), _output);
        Assert.Equal(TrainingRunVerificationStatus.Verified, record.Status);
    }

    [Fact]
    public void StagedInitialization_WithMatchingHashes_IsUnverifiedBecauseLoadingWasNotObserved()
    {
        var record = TrainingRunReportVerifier.Verify(Job("fine_tune", 7, ("G", "aa")),
            Report(observed: Pretrained(("G", "aa")), evidence: TrainingInitializationEvidence.StagedBeforeLaunch), _output);
        Assert.Equal(TrainingRunVerificationStatus.Unverified, record.Status);
        Assert.Contains(record.Problems, x => x.Contains("起動前"));
    }

    [Fact]
    public void UnobservedInitialization_IsUnverified_NotVerified()
    {
        var record = TrainingRunReportVerifier.Verify(Job("fine_tune", 7, ("G", "aa")), Report(), _output);
        Assert.Equal(TrainingRunVerificationStatus.Unverified, record.Status);
        Assert.NotEmpty(record.Problems);
    }

    [Fact]
    public void MissingLegacyOrForeignReports_Fail()
    {
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(Job(), null, _output).Status);
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(Job(), Report(schema: "0"), _output).Status);
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(Job(), Report(jobId: "other"), _output).Status);
    }

    [Fact]
    public void ModeFallback_SeedMismatch_AndNonZeroExit_Fail()
    {
        var fallback = TrainingRunReportVerifier.Verify(Job("fine_tune"), Report("from_scratch"), _output);
        Assert.Equal(TrainingRunVerificationStatus.Failed, fallback.Status);
        Assert.Contains(fallback.Problems, x => x.Contains("学習モードが計画と違います"));
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(Job(seed: 7), Report(seed: 8), _output).Status);
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(Job(), Report(exit: 1), _output).Status);
    }

    [Fact]
    public void FromScratch_ThatLoadedAPretrainedWeight_FailsAndFineTuneWithDifferentWeightsFails()
    {
        var scratch = TrainingRunReportVerifier.Verify(Job("from_scratch"),
            Report("from_scratch", observed: Pretrained(("G", "aa"))), _output);
        Assert.Equal(TrainingRunVerificationStatus.Failed, scratch.Status);
        var wrong = TrainingRunReportVerifier.Verify(Job("fine_tune", 7, ("G", "aa")),
            Report(observed: Pretrained(("G", "bb"))), _output);
        Assert.Equal(TrainingRunVerificationStatus.Failed, wrong.Status);
    }

    [Theory]
    [InlineData("missing.safetensors")]
    [InlineData("../outside.safetensors")]
    [InlineData("C:/x/m.safetensors")]
    public void MissingOutsideAndAbsoluteArtifacts_Fail(string path)
    {
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(Job(), Report(path: path), _output).Status);
    }

    [Fact]
    public void TamperedHashAndEmptyArtifacts_Fail()
    {
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(Job(), Report(sha: Hash("other")), _output).Status);
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(Job(), Report(path: null), _output).Status);
        File.WriteAllText(Path.Combine(_output, "m.safetensors"), "");
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(Job(), Report(), _output).Status);
    }

    [Fact]
    public void Parse_ReadsTheWorkerJsonAndRejectsGarbage()
    {
        using var document = JsonDocument.Parse("""
            {"schema_version":"1","job_id":"job-1","resolved_mode":"fine_tune","seed":7,"trainer_exit_code":0,
             "initialization_observed":null,"artifacts":[{"kind":"model_weights","path":"m.safetensors","sha256":"x"}]}
            """);
        var report = TrainingRunReportVerifier.Parse(document.RootElement);
        Assert.Equal("fine_tune", report!.ResolvedMode);
        Assert.Single(report.Artifacts);
        Assert.Null(TrainingRunReportVerifier.Parse(null));
        using var bad = JsonDocument.Parse("""{"artifacts":"nope"}""");
        Assert.Null(TrainingRunReportVerifier.Parse(bad.RootElement));
    }

    [Fact]
    public void TrainerPayload_CarriesThePlannedInitialization_AndIsNullWithoutAPlan()
    {
        var job = Job("fine_tune", 7, ("G_0.safetensors", "aa"));
        var json = JsonSerializer.Serialize(YourSinger.Process.Processing.Talk.StyleBertVits2TrainerService.InitializationPayload(job));
        Assert.Contains("pretrained_checkpoint", json);
        Assert.Contains("G_0.safetensors", json);
        job.ResolvedPlan = null;
        Assert.Null(YourSinger.Process.Processing.Talk.StyleBertVits2TrainerService.InitializationPayload(job));
    }

    [Fact]
    public void DiffSingerPayload_ResolvesCheckpointsInsideTheWorkspaceOnly()
    {
        var workspace = new YourSinger.Data.Processing.ProjectWorkspace(_output);
        var job = Job("fine_tune", 7, ("acoustic", "aa"));
        var json = JsonSerializer.Serialize(YourSinger.Process.Processing.Singing.DiffSingerTrainerService.InitializationPayload(workspace, job));
        Assert.Contains(Path.GetFullPath(_output).Replace("\\", "\\\\"), json);
        job.ResolvedPlan!.Stages[0].Initialization!.Components[0] = new TrainingInitializationComponent { Name = "acoustic", Sha256 = "aa", Path = "../escape.ckpt" };
        Assert.Throws<InvalidOperationException>(() =>
            YourSinger.Process.Processing.Singing.DiffSingerTrainerService.InitializationPayload(workspace, job));
        job.ResolvedPlan = null;
        Assert.Null(YourSinger.Process.Processing.Singing.DiffSingerTrainerService.InitializationPayload(workspace, job));
    }

    [Fact]
    public async Task DiffSingerTrainer_SendsThePlanAndRecordsTheVerification()
    {
        using var project = new TestProject();
        var output = Path.Combine(project.Workspace.RootPath, "training", "diffsinger", "job-1", "trainer");
        Directory.CreateDirectory(Path.Combine(output, "checkpoints", "job-1"));
        File.WriteAllText(Path.Combine(output, "checkpoints", "job-1", "model_ckpt_steps_1.ckpt"), "trained");
        var sha = Hash("trained");
        var code = $$"""
            import json,sys
            request=json.loads(sys.stdin.readline()); payload=request['payload']
            assert payload['resolved_training_mode']=='from_scratch' and payload['job_id']=='job-1' and payload['seed']==7
            assert payload['initialization']['type']=='random_initialization'
            report=dict(schema_version='1',job_id='job-1',resolved_mode='from_scratch',seed=7,trainer_exit_code=0,
                        initialization_observed=dict(type='random_initialization',components=[]),initialization_evidence='staged_before_launch',
                        artifacts=[dict(kind='checkpoint',path='checkpoints/job-1/model_ckpt_steps_1.ckpt',sha256='{{sha}}')])
            print(json.dumps(dict(request_id=request['request_id'],status='ok',result=dict(log_path='x.log',run_report=report))))
            """;
        var python = Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON") ?? "python";
        var service = new YourSinger.Process.Processing.Singing.DiffSingerTrainerService(
            new YourSinger.Process.Processing.Ml.Bridge.MlWorkerClient(python, ["-u", "-c", code]));
        var job = Job("from_scratch", 7);
        var typed = new TrainingJobRecord
        {
            JobId = "job-1", SpeakerId = "spk_a", Target = TrainingTarget.Singing, DatasetFingerprint = "fp", Seed = 7, ResolvedPlan = job.ResolvedPlan
        };
        await service.TrainAsync(project.Workspace, typed, "config.yaml", TestProject.Token);
        Assert.Equal(TrainingRunVerificationStatus.Unverified, typed.RunVerification!.Status);
    }

    [Fact]
    public void TalkJobsWithAnAssessment_RequireTheWeightedSamplerToHaveRun()
    {
        var job = Job();
        job.InputFingerprint = "fp";
        var missing = TrainingRunReportVerifier.Verify(job, Report(sampled: false), _output);
        Assert.Equal(TrainingRunVerificationStatus.Failed, missing.Status);
        Assert.Contains(missing.Problems, x => x.Contains("重み付きサンプリング"));
        Assert.NotEqual(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(job, Report(), _output).Status);
        var unused = new TrainingRunReport
        {
            SchemaVersion = "1", JobId = "job-1", ResolvedMode = "fine_tune", Seed = 7, TrainerExitCode = 0,
            WeightSampling = new TrainingWeightSamplingRecord { Applied = true, SamplerVersion = "v", Seed = 7, Iterations = 0 },
            Artifacts = [new TrainingRunArtifact { Kind = "w", Path = "m.safetensors", Sha256 = Hash("weights") }]
        };
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(job, unused, _output).Status);
        var wrongSeed = new TrainingRunReport
        {
            SchemaVersion = "1", JobId = "job-1", ResolvedMode = "fine_tune", Seed = 7, TrainerExitCode = 0,
            WeightSampling = new TrainingWeightSamplingRecord { Applied = true, SamplerVersion = "v", Seed = 8, Iterations = 2 },
            Artifacts = [new TrainingRunArtifact { Kind = "w", Path = "m.safetensors", Sha256 = Hash("weights") }]
        };
        Assert.Equal(TrainingRunVerificationStatus.Failed, TrainingRunReportVerifier.Verify(job, wrongSeed, _output).Status);
    }
}
