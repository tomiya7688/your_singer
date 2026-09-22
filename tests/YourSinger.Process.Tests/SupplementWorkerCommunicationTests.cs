using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Tests;

public sealed class SupplementWorkerCommunicationTests
{
    private static MlWorkerClient Client(string code) => new(
        Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON") ?? "python", ["-u", "-c", code]);
    private const string Read = "import json,sys; r=json.loads(sys.stdin.readline()); ";
    private const string Reply = "print(json.dumps(dict(request_id=r['request_id'],status='ok',result={'value':'日本語'})))";

    [Fact]
    public async Task LargeErrorLogDoesNotDeadlockJsonResponse()
    {
        var result = await Client(Read + "sys.stderr.write('x'*1000000); sys.stderr.flush(); " + Reply)
            .SendAsync("test", new { text = "日本語" }, TestProject.Token).WaitAsync(TimeSpan.FromSeconds(20), TestProject.Token);
        Assert.Equal("日本語", result.GetProperty("value").GetString());
    }

    [Fact]
    public async Task WrongRequestIdIsRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(Read + "r['request_id']='wrong'; " + Reply)
            .SendAsync("test", new { }, TestProject.Token));
    }

    [Fact]
    public async Task NonzeroExitIsNotSuccessEvenWithValidJson()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(Read + Reply + "; sys.exit(5)")
            .SendAsync("test", new { }, TestProject.Token));
    }

    [Fact]
    public async Task CancelStopsWorker()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestProject.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(Read + "import time; time.sleep(60)")
            .SendAsync("test", new { }, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15), TestProject.Token));
    }
}
