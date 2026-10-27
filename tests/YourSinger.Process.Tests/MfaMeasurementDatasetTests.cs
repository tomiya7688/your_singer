using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Tests;

// {
//   責務: [MfaMeasurementDatasetTests: 測定参照の保存、音声との同一性、学習入力への境界を確認する]
//   処理: [1: 合成素材を準備する 2: 正常・変更・欠落の各境界を検証する]
// }
public sealed class MfaMeasurementDatasetTests
{
    private static CancellationToken Token => TestProject.Token;
    private static SpeakerTrainingSelection Singing => new() { SpeakerId = "spk_a", Target = TrainingTarget.Singing, AutoCorrectionEnabled = false };

    // { 責務: [ReferenceRoundTripsToSingingDataset: 参照が保存され歌唱入力まで届くことを確認する] }
    [Fact]
    public async Task ReferenceRoundTripsToSingingDataset()
    {
        using var p = new TestProject();
        var segment = p.Segment("record", type: SegmentContentType.Singing);
        await p.SeedAsync(segment);
        var saved = Assert.Single((await p.DatasetRepository.LoadAsync(p.Workspace, Token))!.Segments);
        Assert.NotNull(saved.AlignmentMeasurement);
        Assert.Equal(segment.AlignmentMeasurement!.Sha256, saved.AlignmentMeasurement.Sha256);
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Singing], Token)).Jobs);
        var result = await new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, Token);
        Assert.Equal(saved.AlignmentMeasurement.AudioSha256, Assert.Single(result.Items).AlignmentMeasurement!.AudioSha256);
        var jsonPath = Path.Combine(p.Workspace.RootPath, "training", "diffsinger", job.JobId, "dataset.json");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, Token));
        Assert.Equal(saved.AlignmentMeasurement.Path, json.RootElement.GetProperty("items")[0].GetProperty("alignment_measurement").GetProperty("path").GetString());
    }

    // { 責務: [AcousticPreparationRejectsChangedDataset: 生成済み項目の変更を音響変換前に拒否する] }
    [Fact]
    public async Task AcousticPreparationRejectsChangedDataset()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("record", type: SegmentContentType.Singing));
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Singing], Token)).Jobs);
        await new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, Token);
        await DiffSingerAcousticDatasetValidator.ValidateAsync(p.Workspace, job, Token);
        var path = Path.Combine(p.Workspace.RootPath, "training", "diffsinger", job.JobId, "dataset.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!;
        document["items"]![0]!["phonemes"]![0] = "changed";
        await File.WriteAllTextAsync(path, document.ToJsonString(), Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => DiffSingerAcousticDatasetValidator.ValidateAsync(p.Workspace, job, Token));
    }
    // { 責務: [AcousticPreparationRejectsMalformedDataset: 壊れたJSONと欠落リストを再生成エラーとして扱う] }
    [Theory]
    [InlineData("json")]
    [InlineData("items")]
    [InlineData("phonemes")]
    [InlineData("item")]
    [InlineData("phone")]
    [InlineData("setting")]
    public async Task AcousticPreparationRejectsMalformedDataset(string change)
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("record", type: SegmentContentType.Singing));
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Singing], Token)).Jobs);
        await new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, Token);
        var path = Path.Combine(p.Workspace.RootPath, "training", "diffsinger", job.JobId, "dataset.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, Token))!;
        if (change == "items") document["items"] = null;
        if (change == "phonemes") document["items"]![0]!["phonemes"] = null;
        if (change == "item") document["items"]![0] = null;
        if (change == "phone") document["items"]![0]!["phonemes"]![0] = null;
        if (change == "setting") document.AsObject().Remove("auto_correction_enabled");
        await File.WriteAllTextAsync(path, change == "json" ? "{" : document.ToJsonString(), Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => DiffSingerAcousticDatasetValidator.ValidateAsync(p.Workspace, job, Token));
    }
    // { 責務: [OpenedHandleMustMatchCheckedPath: 開いた実体が検査済みパスと違うと拒否する] }
    [Fact]
    public void OpenedHandleMustMatchCheckedPath()
    {
        using var p = new TestProject();
        var actual = Path.Combine(p.Workspace.RootPath, "actual.txt");
        var expected = Path.Combine(p.Workspace.RootPath, "expected.txt");
        File.WriteAllText(actual, "actual");
        File.WriteAllText(expected, "expected");
        using var stream = File.OpenRead(actual);
        VerifiedFileInput.RequireExpected(stream, actual);
        Assert.Throws<InvalidDataException>(() => VerifiedFileInput.RequireExpected(stream, expected));
        Assert.Throws<FileNotFoundException>(() => VerifiedFileInput.OpenRead(Path.Combine(p.Workspace.RootPath, "missing.txt")));
    }

    // { 責務: [AcousticPreparationUsesWorkerWithVerifiedDataset: 合成データをC#と実workerの両方で照合する] }
    [Fact]
    public async Task AcousticPreparationUsesWorkerWithVerifiedDataset()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("record", type: SegmentContentType.Singing));
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Singing], Token)).Jobs);
        await new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, Token);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "src", "process")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var worker = Path.Combine(repository.FullName, "src", "process", "YourSinger.Process", "processing", "ml", "worker");
        var python = Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON") ?? "python";
        var code = "import runpy,sys;sys.path.insert(0,sys.argv[1]);runpy.run_module('your_singer_ml.main',run_name='__main__')";
        var service = new DiffSingerAcousticInputService(new MlWorkerClient(python, ["-c", code, worker]));
        var output = await service.PrepareAsync(p.Workspace, job, Token);
        Assert.True(File.Exists(Path.Combine(output, "transcriptions.csv")));
        Assert.True(File.Exists(Path.Combine(output, "provenance.json")));
    }

    // { 責務: [LinuxFifoOpenDoesNotBlock: Linuxで検査後にFIFOへ差し替えられても待機しない] }
    [Fact]
    public async Task LinuxFifoOpenDoesNotBlock()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var p = new TestProject();
        var fifo = Path.Combine(p.Workspace.RootPath, "input-fifo");
        // { 説明: [ownerReadWrite: POSIXの0600] }
        const uint ownerReadWrite = 0x180;
        Assert.Equal(0, MakeFifo(fifo, ownerReadWrite));
        var error = await Record.ExceptionAsync(async () => await Task.Run(() =>
        {
            using var stream = VerifiedFileInput.OpenRead(fifo);
            VerifiedFileInput.RequireRegular(stream);
        }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.True(error is InvalidDataException or NotSupportedException or IOException or ArgumentException, $"予期しない結果: {error?.GetType().Name}");
        Assert.Throws<InvalidDataException>(() => MfaMeasurementFiles.OpenRead(p.Workspace, "input-fifo", 1024));
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(string path, uint mode);

    // { 責務: [ChangedMeasurementCannotEnterTraining: 変更・欠落・領域外・条件不一致を拒否する] }
    [Theory]
    [InlineData("記録変更")]
    [InlineData("記録欠落")]
    [InlineData("音声変更")]
    [InlineData("領域外")]
    [InlineData("モデル版")]
    [InlineData("音素")]
    [InlineData("元ラベル")]
    [InlineData("時刻")]
    [InlineData("音声長")]
    public async Task ChangedMeasurementCannotEnterTraining(string change)
    {
        using var p = new TestProject();
        var segment = p.Segment("record", type: SegmentContentType.Singing);
        await p.SeedAsync(segment);
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace, [Singing], Token)).Jobs);
        var recordPath = Path.Combine(p.Workspace.RootPath, segment.AlignmentMeasurement!.Path);
        if (change == "記録変更") await File.AppendAllTextAsync(recordPath, " ", Token);
        else if (change == "記録欠落") File.Delete(recordPath);
        else if (change == "音声変更") TestProject.WriteWave(Path.Combine(p.Workspace.RootPath, segment.AudioPath), 0.1);
        else if (change == "領域外")
        {
            var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
            dataset.Segments[0].AlignmentMeasurement = new() { Path = "../outside.json", Sha256 = segment.AlignmentMeasurement.Sha256, AudioSha256 = segment.AlignmentMeasurement.AudioSha256 };
            await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
        }
        else await ChangeAndRehashAsync(p, recordPath, change);
        if (change == "記録欠落")
            await Assert.ThrowsAsync<FileNotFoundException>(() => new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, Token));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.RootPath, "training")));
    }

    // { 責務: [MissingLegacyReferenceCannotPlanSinging: 旧キャッシュは実測済みとして利用しない] }
    [Fact]
    public async Task MissingLegacyReferenceCannotPlanSinging()
    {
        using var p = new TestProject();
        var segment = p.Segment("old", type: SegmentContentType.Singing);
        await p.SeedAsync(segment);
        var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        dataset.Segments[0].AlignmentMeasurement = null;
        await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.Planner().CreateBatchAsync(p.Workspace, [Singing], Token));
    }

    // { 責務: [ChangeAndRehashAsync: 記録を変更しhashも更新して意味の不一致を再現する] }
    private static async Task ChangeAndRehashAsync(TestProject p, string recordPath, string change)
    {
        var node = JsonNode.Parse(await File.ReadAllTextAsync(recordPath, Token))!;
        if (change == "モデル版") node["conditions"]!["mfa_version"] = "wrong";
        if (change == "音素") node["phonemes"]![0]!["phoneme"] = "i";
        if (change == "元ラベル") node["mfa_intervals"]![0]!["label"] = "sil";
        if (change == "時刻") node["phonemes"]![0]!["start_sec"] = 0.1;
        if (change == "音声長") node["duration_sec"] = 9.0;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        await File.WriteAllBytesAsync(recordPath, bytes, Token);
        var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        var old = dataset.Segments[0].AlignmentMeasurement!;
        dataset.Segments[0].AlignmentMeasurement = new() { Path = old.Path, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), AudioSha256 = old.AudioSha256 };
        await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
    }
}
