using System.Text;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Singing;

namespace YourSinger.Process.Tests;

// {
//   責務: [DiffSingerDurationInputServiceTests: workerへの要求・保存先照合・事前条件の拒否を確認する]
//   補足: [本物のworkerではなく、要求を検査する代替workerで境界だけ確認する。Duration生成自体はtests/workerで確認する]
// }
public sealed class DiffSingerDurationInputServiceTests
{
    private static readonly SpeakerTrainingSelection Singing = new()
    {
        SpeakerId = "spk_a",
        Target = TrainingTarget.Singing,
        AutoCorrectionEnabled = false
    };

    private static async Task<(TestProject, TrainingJobRecord)> ProjectAsync()
    {
        var project = new TestProject();
        await project.SeedAsync(project.Segment("a", type: SegmentContentType.Singing));
        var job = Assert.Single((await project.Planner().CreateBatchAsync(project.Workspace, [Singing], TestProject.Token)).Jobs);
        return (project, job);
    }

    private static async Task<MlWorkerClient> ClientAsync(TestProject project, string body)
    {
        var script = Path.Combine(project.Workspace.RootPath, "duration_worker.py");
        await File.WriteAllTextAsync(script,
            "import json, sys\nr = json.loads(sys.stdin.readline())\n" + body, new UTF8Encoding(false), TestProject.Token);
        return new MlWorkerClient(Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON"), [script]);
    }

    [Fact]
    public async Task ARequestWithoutAcousticInputIsRejectedBeforeCallingTheWorker()
    {
        var (project, job) = await ProjectAsync();
        using (project)
        {
            var service = new DiffSingerDurationInputService(new MlWorkerClient("worker-that-must-not-start", []));
            await Assert.ThrowsAsync<FileNotFoundException>(() => service.PrepareAsync(project.Workspace, job, TestProject.Token));
        }
    }

    [PythonWorkerFact]
    public async Task TheWorkerIsAskedWithJobIdentityAndItsOutputLocationIsChecked()
    {
        var (project, job) = await ProjectAsync();
        using (project)
        {
            var root = Path.Combine(project.Workspace.RootPath, "training", "diffsinger", job.JobId);
            Directory.CreateDirectory(Path.Combine(root, "acoustic-input"));
            await File.WriteAllTextAsync(Path.Combine(root, "acoustic-input", "provenance.json"), "{}", TestProject.Token);
            var wrong = await ClientAsync(project,
                "print(json.dumps({'request_id': r['request_id'], 'status': 'ok', 'result': {'raw_data_dir': 'elsewhere'}}))\n");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new DiffSingerDurationInputService(wrong).PrepareAsync(project.Workspace, job, TestProject.Token));

            var good = await ClientAsync(project,
                "p = r['payload']\nassert r['command'] == 'prepare_diffsinger_duration' and p['job_id'] and p['dataset_fingerprint']\n" +
                "import os\nd = os.path.join(p['project_root'], 'training', 'diffsinger', p['job_id'], 'duration-input')\nos.makedirs(d)\n" +
                "open(os.path.join(d, 'transcriptions.csv'), 'w').write('')\nopen(os.path.join(d, 'provenance.json'), 'w').write('{}')\n" +
                "print(json.dumps({'request_id': r['request_id'], 'status': 'ok', 'result': {'raw_data_dir': d}}))\n");
            var directory = await new DiffSingerDurationInputService(good).PrepareAsync(project.Workspace, job, TestProject.Token);
            Assert.True(File.Exists(Path.Combine(directory, "transcriptions.csv")));
        }
    }
}
