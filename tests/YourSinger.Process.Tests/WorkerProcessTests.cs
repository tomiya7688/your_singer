using System.Text;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Tests;

public sealed class PythonWorkerFactAttribute : FactAttribute
{
    public PythonWorkerFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        // CI(GitHub Actions)で変数が空のときは、黙ってスキップして緑にならないよう、スキップせず失敗させる。
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON")) &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
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
        var sending = client.SendAsync("test", new { pid_path = pidPath }, canceled.Token);

        // ワーカーがPIDを書き終えてからキャンセルする(起動が遅いCIでも、書きかけのファイルを読まない)。
        var pid = 0;
        for (var attempt = 0; attempt < 300 && pid == 0; attempt++)
        {
            if (File.Exists(pidPath) && int.TryParse(
                    await File.ReadAllTextAsync(pidPath, Token),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
                pid = parsed;
            else
                await Task.Delay(50, Token);
        }
        Assert.True(pid > 0, "worker did not publish its PID");

        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            Assert.True(process.WaitForExit(5000), "worker process was not terminated after cancellation");
        }
        catch (ArgumentException)
        {
            // 終了済みでOSのプロセス一覧に存在しない。
        }
    }
}
