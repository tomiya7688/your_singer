using YourSinger.Data.Models;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Talk;

namespace YourSinger.Process.Tests;

// {
//   責務: [StyleBertVits2TrainingTests: 公式前処理への入力とTrainer要求を確認する]
//   処理: [1: 合成データを計画する 2: パス形式とIPC要求を確認する]
// }
public sealed class StyleBertVits2TrainingTests
{
    // { 責務: [TalkListUsesPathsRelativeToRawDirectory: 公式がwavs/を付ける前の音声名を確認する] }
    [Fact]
    public async Task TalkListUsesPathsRelativeToRawDirectory()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("sample"));
        var job = await CreateJob(project);
        var dataset = await new StyleBertVits2DatasetBuilder(project.DatasetRepository)
            .BuildAsync(project.Workspace, job, "話者", TestProject.Token);
        var audio = Assert.Single(dataset.Items);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(audio.AudioPath))!;
        Assert.Equal("sample.wav|話者|JP|あいうえお", Assert.Single(await File.ReadAllLinesAsync(Path.Combine(root, "esd.list"), TestProject.Token)));
        Assert.True(File.Exists(Path.Combine(root, "raw", "sample.wav")));
    }

    // { 責務: [UnsafeTalkColumnsDoNotPublishInput: 列の区切りを含む話者名で入力が公開されないことを確認する] }
    [Theory]
    [InlineData("話|者")]
    [InlineData("話\n者")]
    [InlineData("")]
    public async Task UnsafeTalkColumnsDoNotPublishInput(string speakerName)
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("sample"));
        var job = await CreateJob(project);
        await Assert.ThrowsAsync<InvalidDataException>(() => new StyleBertVits2DatasetBuilder(project.DatasetRepository)
            .BuildAsync(project.Workspace, job, speakerName, TestProject.Token));
        Assert.False(Directory.Exists(Path.Combine(project.Workspace.RootPath, "training")));
    }

    // { 責務: [TrainerReceivesThePreparedDatasetAndJobIdentity: ワーカーへ入力と補正設定が渡ることを確認する] }
    [Fact]
    public async Task TrainerReceivesThePreparedDatasetAndJobIdentity()
    {
        using var project = new TestProject();
        await project.SeedAsync(project.Segment("sample"));
        var job = await CreateJob(project);
        await new StyleBertVits2DatasetBuilder(project.DatasetRepository)
            .BuildAsync(project.Workspace, job, "話者", TestProject.Token);
        // 合成ワーカーは学習せず、アプリからの入力契約だけを照合する。
        var code = """
            import json,sys
            from pathlib import Path
            request=json.loads(sys.stdin.readline()); payload=request['payload']
            assert request['command']=='train_style_bert_vits2'
            dataset=Path(payload['dataset_dir']); record=json.loads((dataset/'dataset.json').read_text(encoding='utf-8'))
            for key in ['job_id','dataset_fingerprint','auto_correction_enabled']:
                assert payload[key]==record[key]
            assert Path(payload['output_dir'])==dataset/'trainer'
            print(json.dumps(dict(request_id=request['request_id'],status='ok',result=dict(log_path=str(dataset/'trainer'/'trainer.log')))))
            """;
        var python = Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON") ?? "python";
        var service = new StyleBertVits2TrainerService(new MlWorkerClient(python, ["-u", "-c", code]));
        var log = await service.TrainAsync(project.Workspace, job, "話者モデル", TestProject.Token);
        Assert.EndsWith(Path.Combine(job.JobId, "trainer", "trainer.log"), log);
    }

    // { 責務: [CreateJob: 補正OFFの合成トーク学習ジョブを作成する] }
    private static async Task<TrainingJobRecord> CreateJob(TestProject project) => Assert.Single((await project.Planner()
        .CreateBatchAsync(project.Workspace, [new SpeakerTrainingSelection
        {
            SpeakerId = "spk_a", Target = TrainingTarget.Talk, AutoCorrectionEnabled = false
        }], TestProject.Token)).Jobs);
}
