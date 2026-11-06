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
        string? sha = null, TrainingInitialization? observed = null, string schema = "1", string jobId = "job-1") => new()
    {
        SchemaVersion = schema, JobId = jobId, ResolvedMode = mode, Seed = seed, TrainerExitCode = exit, InitializationObserved = observed,
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
}
