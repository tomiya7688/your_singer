using System.Text;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// { 責務: [DiffSingerDurationBinarizeServiceTests: 事前条件の拒否と、workerが返す設定パスの領域検査を確認する] }
public sealed class DiffSingerDurationBinarizeServiceTests
{
    private static TrainingJobRecord Job(TrainingTarget target = TrainingTarget.Singing) => new()
    {
        JobId = "j",
        SpeakerId = "spk_a",
        Target = target,
        DatasetFingerprint = "fp",
        SegmentIds = ["s"],
        Backend = TrainingBackendCatalog.SingingBackend,
        BackendRevision = TrainingBackendCatalog.SingingRevision
    };

    [Fact]
    public async Task WithoutDurationInputOrForTalkTheWorkerIsNeverStarted()
    {
        using var project = new TestProject();
        var service = new DiffSingerDurationBinarizeService(new MlWorkerClient("worker-that-must-not-start", []));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.BinarizeAsync(project.Workspace, Job(), "trainer", TestProject.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BinarizeAsync(project.Workspace, Job(TrainingTarget.Talk), "trainer", TestProject.Token));
    }

    [PythonWorkerFact]
    public async Task ASettingPathOutsideTheJobAreaIsRejected()
    {
        using var project = new TestProject();
        var directory = Path.Combine(project.Workspace.RootPath, "training", "diffsinger", "j", "duration-input");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "transcriptions.csv"), "name\n", TestProject.Token);
        var script = Path.Combine(project.Workspace.RootPath, "w.py");
        await File.WriteAllTextAsync(script,
            "import json, sys\nr = json.loads(sys.stdin.readline())\n" +
            "print(json.dumps({'request_id': r['request_id'], 'status': 'ok', 'result': {'config_path': __file__}}))\n",
            new UTF8Encoding(false), TestProject.Token);
        var client = new MlWorkerClient(Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON"), [script]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DiffSingerDurationBinarizeService(client).BinarizeAsync(project.Workspace, Job(), "trainer", TestProject.Token));
    }
}
