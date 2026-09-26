using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Tests;

public sealed class SupplementWorkerUnicodeTests
{
    [Fact]
    public async Task UnescapedJapaneseJsonIsUtf8OnEveryPlatform()
    {
        var python = Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON") ?? "python";
        var code = "import json,sys,os; r=json.loads(sys.stdin.readline()); " +
            "assert os.environ['PYTHONIOENCODING']=='utf-8'; " +
            "print(json.dumps(dict(request_id=r['request_id'],status='ok',result={'text':'日本語の補完候補'}),ensure_ascii=False))";
        var response = await new MlWorkerClient(python, ["-u", "-c", code])
            .SendAsync("test", new { }, TestProject.Token);
        Assert.Equal("日本語の補完候補", response.GetProperty("text").GetString());
    }
}
