using System.Security.Cryptography;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// { 責務: [VerificationDisplayTests: 学習後の検証記録の読み取りと日本語の状態表示を確認する(検証未実施・失敗・注意・正常音声・欠損・旧履歴)] }
public sealed class VerificationDisplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ys-verif-" + Guid.NewGuid().ToString("N"));
    private ProjectWorkspace Workspace => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Write(string jobId, string json)
    {
        var dir = TrainingVerificationStore.DirectoryOf(Workspace, jobId)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "verification.json"), json);
        return dir;
    }

    private static string Q(string text) => text.Replace('\'', '"');

    private static string Json(string jobId, string stages, string artifacts = "[]", string extra = "") =>
        Q($"{{'schema_version':'1','job_id':'{jobId}','stages':{{{stages}}},'artifacts':{artifacts}{extra}}}");

    private static readonly string AllPassed =
        Q("'checkpoint':{'state':'passed'},'export':{'state':'passed'},'inference':{'state':'passed'}");

    [Fact]
    public void AnOldJobWithoutARecordIsShownAsUnverifiedNeverAvailable()
    {
        var load = TrainingVerificationStore.Load(Workspace, "job-old");
        Assert.Equal(VerificationLoadStatus.Missing, load.Status);
        Assert.Equal("学習完了・未検証", TrainingVerificationPresenter.Overall(TrainingJobState.Succeeded, load));
        var text = string.Join("\n", TrainingVerificationPresenter.Describe(TrainingJobState.Succeeded, load, []));
        Assert.Contains("未実施として扱い", text);
        Assert.DoesNotContain("利用可能", text.Replace("利用可能とは表示しません", ""));
    }

    [Fact]
    public void CorruptOrMismatchedRecordsAreInvalidAndTreatedAsUnverified()
    {
        Write("job-bad", "{not json");
        Write("job-ver", Q("{'schema_version':'9','job_id':'job-ver','stages':{}}"));
        Write("job-other", Json("someone-else", AllPassed));
        foreach (var id in new[] { "job-bad", "job-ver", "job-other" })
        {
            var load = TrainingVerificationStore.Load(Workspace, id);
            Assert.Equal(VerificationLoadStatus.Invalid, load.Status);
            Assert.Contains("未検証", TrainingVerificationPresenter.Overall(TrainingJobState.Succeeded, load));
        }
        Assert.Equal(VerificationLoadStatus.Invalid, TrainingVerificationStore.Load(Workspace, "../escape").Status);
    }

    [Fact]
    public void AvailableRequiresCheckpointExportAndInferenceAllPassedAndExternalIsShownSeparately()
    {
        Write("job-ok", Json("job-ok", AllPassed + Q(",'external_compatibility':{'state':'not_run','reasons':['runner_unavailable']}")));
        var load = TrainingVerificationStore.Load(Workspace, "job-ok");
        Assert.Equal("利用可能", TrainingVerificationPresenter.Overall(TrainingJobState.Succeeded, load));
        var text = string.Join("\n", TrainingVerificationPresenter.Describe(TrainingJobState.Succeeded, load, []));
        Assert.Contains("外部互換: 未実施(実行環境がありません)", text);
        Assert.Contains("品質評価: 未実施", text);

        // 推論が実行されていない/測定不能なら、他が合格でも利用可能にしない。
        Write("job-noinf", Json("job-noinf", Q("'checkpoint':{'state':'passed'},'export':{'state':'passed'},'inference':{'state':'unavailable'}")));
        Assert.Equal("学習完了・検証待ち",
            TrainingVerificationPresenter.Overall(TrainingJobState.Succeeded, TrainingVerificationStore.Load(Workspace, "job-noinf")));
    }

    [Fact]
    public void FailureWarningAndRunningAreNotDisguisedAsAvailable()
    {
        Write("job-fail", Json("job-fail", Q("'checkpoint':{'state':'passed'},'export':{'state':'passed'},'inference':{'state':'failed','reasons':['silent_output']}")));
        Write("job-warn", Json("job-warn", AllPassed + Q(",'quality':{'state':'warning','reasons':['low_intelligibility']}")));
        Write("job-run", Json("job-run", Q("'checkpoint':{'state':'passed'},'export':{'state':'passed'},'inference':{'state':'running'}")));
        Assert.Equal("検証失敗", TrainingVerificationPresenter.Overall(TrainingJobState.Succeeded, TrainingVerificationStore.Load(Workspace, "job-fail")));
        Assert.Equal("注意あり", TrainingVerificationPresenter.Overall(TrainingJobState.Succeeded, TrainingVerificationStore.Load(Workspace, "job-warn")));
        Assert.Equal("生成試験中", TrainingVerificationPresenter.Overall(TrainingJobState.Succeeded, TrainingVerificationStore.Load(Workspace, "job-run")));
        var failText = string.Join("\n", TrainingVerificationPresenter.Describe(TrainingJobState.Succeeded,
            TrainingVerificationStore.Load(Workspace, "job-fail"), []));
        Assert.Contains("推論試験(実音声生成): 失敗(ほぼ無音)", failText);
        // 学習器の成功は、検証の成功とは別に表示する。
        Assert.Contains("学習: 完了", failText);
        Assert.Equal("学習中", TrainingVerificationPresenter.Overall(TrainingJobState.Running, TrainingVerificationStore.Load(Workspace, "job-ok")));
    }

    [Fact]
    public void SamplesAreResolvedInsideTheVerificationFolderWithHashAndMissingChecks()
    {
        var dir = Write("job-s", "{}");
        Directory.CreateDirectory(Path.Combine(dir, "samples"));
        var good = Path.Combine(dir, "samples", "a.wav");
        File.WriteAllBytes(good, [1, 2, 3, 4]);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(good)));
        File.WriteAllBytes(Path.Combine(dir, "samples", "changed.wav"), [9, 9]);
        File.WriteAllBytes(Path.Combine(dir, "samples", "nohash.wav"), [5]);
        var outside = Path.Combine(_root, "outside.wav");
        File.WriteAllBytes(outside, [7]);
        var artifacts = $$"""
            [{"kind":"sample_wav","path":"samples/a.wav","audio_sha256":"{{hash}}","label":"短文"},
             {"kind":"sample_wav","path":"samples/missing.wav","audio_sha256":"{{hash}}"},
             {"kind":"sample_wav","path":"samples/changed.wav","audio_sha256":"{{hash}}"},
             {"kind":"sample_wav","path":"samples/nohash.wav"},
             {"kind":"sample_wav","path":"../../../outside.wav","audio_sha256":"{{hash}}"},
             {"kind":"log","path":"samples/a.wav"}]
            """;
        Write("job-s", Json("job-s", AllPassed, artifacts));
        var load = TrainingVerificationStore.Load(Workspace, "job-s");
        var samples = TrainingVerificationStore.Samples(load);
        Assert.Equal(
            [SampleStatus.Playable, SampleStatus.FileMissing, SampleStatus.HashMismatch, SampleStatus.HashNotRecorded, SampleStatus.OutsideVerificationFolder],
            samples.Select(x => x.Status).ToArray());
        Assert.Equal("短文", samples[0].Label);
        Assert.Equal(good, samples[0].FullPath);
        Assert.Equal(new[] { true, false, false, true, false }, samples.Select(TrainingVerificationPresenter.IsPlayable).ToArray());
        var text = string.Join("\n", TrainingVerificationPresenter.Describe(TrainingJobState.Succeeded, load, samples));
        Assert.Contains("ファイルがありません(再生できません)", text);
        Assert.Contains("保存後に内容が変わっています(再生しません)", text);
    }

    [Fact]
    public void CheckpointSelectionIsPendingUnlessACandidateIsActuallySelected()
    {
        Assert.Contains("保留", TrainingVerificationPresenter.DescribeSelection(null)[0]);
        var pending = new CheckpointSelectionRecord
        {
            State = "pending",
            Candidates = [new CheckpointCandidateRecord { Id = "step-1000", Eligible = true }]
        };
        var pendingText = string.Join("\n", TrainingVerificationPresenter.DescribeSelection(pending));
        Assert.Contains("保留", pendingText);
        Assert.DoesNotContain("選定理由", pendingText);
        // 選定済みと書かれていても、候補に存在しないIDは選定として扱わない。
        var ghost = new CheckpointSelectionRecord { State = "selected", SelectedId = "nope", Candidates = pending.Candidates };
        Assert.Contains("保留", TrainingVerificationPresenter.DescribeSelection(ghost)[0]);
        var selected = new CheckpointSelectionRecord
        {
            State = "selected",
            SelectedId = "step-1000",
            Reason = "発音と音程が最良",
            Candidates = [new CheckpointCandidateRecord { Id = "step-1000", Eligible = true }, new CheckpointCandidateRecord { Id = "step-500", Eligible = false, Reason = "無音" }]
        };
        var text = string.Join("\n", TrainingVerificationPresenter.DescribeSelection(selected));
        Assert.Contains("checkpoint選定: step-1000 / 選定理由: 発音と音程が最良", text);
        Assert.Contains("候補 step-500: 不適格 / 無音", text);
    }
}
