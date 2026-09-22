using System.Text;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Tests;

public sealed class PythonWorkerFactAttribute : FactAttribute
{
    public PythonWorkerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON")))
            Skip = "プロセス境界テストにはYOURSINGER_TEST_PYTHONの指定が必要です。";
    }
}

public sealed class WorkerProcessTests
{
    private static CancellationToken Token => TestProject.Token;
    private static async Task<MlWorkerClient> ClientAsync(TestProject project, string body)
    {
        var script = Path.Combine(project.Workspace.RootPath, "通信検証.py");
        await File.WriteAllTextAsync(script,
            "import json, os, sys, time\nr = json.loads(sys.stdin.readline())\n" + body,
            new UTF8Encoding(false), Token);
        return new MlWorkerClient(Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON"), [script]);
    }

    [PythonWorkerFact]
    public async Task LargeStderrCannotBlockJsonResponse()
    {
        using var p = new TestProject();
        var client = await ClientAsync(p, "sys.stderr.write('x' * 300000)\nsys.stderr.flush()\n" +
            "print(json.dumps({'request_id': r['request_id'], 'status': 'ok', 'result': {'done': True}}))\n");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await client.SendAsync("test", new { }, timeout.Token);
        Assert.True(result.GetProperty("done").GetBoolean());
    }

    [PythonWorkerFact]
    public async Task MismatchedRequestIdIsRejected()
    {
        using var p = new TestProject();
        var client = await ClientAsync(p, "print(json.dumps({'request_id': 'wrong', 'status': 'ok', 'result': {}}))\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SendAsync("test", new { }, Token));
    }

    [PythonWorkerFact]
    public async Task NonzeroExitIsRejectedEvenWithOkResponse()
    {
        using var p = new TestProject();
        var client = await ClientAsync(p,
            "print(json.dumps({'request_id': r['request_id'], 'status': 'ok', 'result': {}}))\nsys.exit(7)\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("test", new { }, Token));
    }

    [PythonWorkerFact]
    public async Task CancellationTerminatesWorkerProcess()
    {
        using var p = new TestProject();
        var pidPath = Path.Combine(p.Workspace.RootPath, "pid.txt");
        var client = await ClientAsync(p,
            "with open(r['payload']['pid_path'], 'w') as f:\n    f.write(str(os.getpid()))\ntime.sleep(60)\n");
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        canceled.CancelAfter(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendAsync("test", new { pid_path = pidPath }, canceled.Token));
        var pid = int.Parse(await File.ReadAllTextAsync(pidPath, Token), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // 終了済みでOSのプロセス一覧に存在しない。
        }
    }
}
