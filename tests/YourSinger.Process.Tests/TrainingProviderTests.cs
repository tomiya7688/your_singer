using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Ml.Bridge;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 第三者学習Worker Providerの宣言検証・登録・有効化・実行の検証を確認する。実際の学習アルゴリズムは対象外(偽worker)。
public sealed class TrainingProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "YourSinger.Tests", Guid.NewGuid().ToString("N"));
    private static readonly string Python = Environment.GetEnvironmentVariable("YOURSINGER_TEST_PYTHON") ?? "python";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Manifest(string id = "acme.provider", string version = "1.0.0", string permissions = """["write_job_artifacts"]""",
        string protocol = "1", string entry = "worker.py", string extra = "", string resources = "") => $$"""
        {"schema_version":"1","provider_id":"{{id}}","version":"{{version}}","protocol_version":"{{protocol}}",
         "display_name_ja":"テストProvider","runtime":{"kind":"external_process","entry":"{{entry}}"},
         "operations":[{"operation_id":"acme.train","version":"1.0.0"}],"permissions_requested":{{permissions}},
         "license_file":"LICENSE.txt"{{resources}}{{extra}}}
        """;

    // 偽worker: 標準入力の要求を読み、mode に応じた応答を返す。
    private const string WorkerCode = """
        import json, sys, hashlib, os, time
        request = json.loads(sys.stdin.readline())
        payload = request['payload']; mode = payload['input'].get('mode', 'ok')
        out = payload['job_output_dir']
        def write(name, text):
            path = os.path.join(out, name); open(path, 'w', encoding='utf-8').write(text)
            return dict(path=name, sha256=hashlib.sha256(text.encode('utf-8')).hexdigest())
        def reply(result): print(json.dumps(dict(request_id=request['request_id'], status='ok', result=result)))
        base = dict(protocol_version='1', operation_id=payload['operation_id'])
        if mode == 'hang': time.sleep(60)
        elif mode == 'malformed': print('not json')
        elif mode == 'protocol': reply(dict(base, protocol_version='2', artifacts=[]))
        elif mode == 'badhash':
            artifact = write('a.txt', 'x'); artifact['sha256'] = '0' * 64; reply(dict(base, artifacts=[artifact]))
        elif mode == 'unexpected': a = write('a.txt', 'x'); write('extra.txt', 'y'); reply(dict(base, artifacts=[a]))
        elif mode == 'escape': reply(dict(base, artifacts=[dict(path='../outside.txt', sha256='0' * 64)]))
        else: reply(dict(base, artifacts=[write('a.txt', 'x')]))
        """;

    private string MakeProvider(string name, string? manifest = null, bool license = true, string? resourcesJson = null,
        string? resourceFileText = "weights")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "worker.py"), WorkerCode);
        if (license) File.WriteAllText(Path.Combine(dir, "LICENSE.txt"), "MIT");
        if (resourceFileText is not null) File.WriteAllText(Path.Combine(dir, "model.bin"), resourceFileText);
        if (resourcesJson is not null) File.WriteAllText(Path.Combine(dir, "resources.json"), resourcesJson);
        File.WriteAllText(Path.Combine(dir, "provider.json"), manifest ?? Manifest());
        return dir;
    }

    private static PluginWorkerAdapter Adapter(TrainingProviderRegistry registry, double seconds = 20) =>
        new(registry, TimeSpan.FromSeconds(seconds), path => new MlWorkerClient(Python, ["-u", path]));

    private static TrainingStrategyReference Ref(TrainingProviderEntry entry) => new()
    {
        StrategyId = "acme.strategy", Version = "1.0.0", Hash = "h", ProviderId = entry.Provider.ProviderId,
        ProviderVersion = entry.Provider.Version, ProviderHash = entry.Hash
    };

    private (TrainingProviderRegistry Registry, TrainingProviderEntry Entry) Enabled(string name = "p")
    {
        var registry = new TrainingProviderRegistry();
        var result = registry.Register(MakeProvider(name));
        Assert.True(result.Accepted, result.Failure);
        registry.Enable("acme.provider", "1.0.0", result.Entry!.Origin);
        return (registry, result.Entry);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("permission")]
    [InlineData("entry_parent")]
    [InlineData("entry_absolute")]
    [InlineData("unknown_field")]
    [InlineData("builtin_id")]
    public void InvalidManifests_AreRejectedIndividually(string kind)
    {
        var manifest = kind switch
        {
            "protocol" => Manifest(protocol: "2"),
            "permission" => Manifest(permissions: """["read_everything"]"""),
            "entry_parent" => Manifest(entry: "../outside.exe"),
            "entry_absolute" => Manifest(entry: "C:/Windows/x.exe"),
            "unknown_field" => Manifest(extra: ",\"unknown\":1"),
            _ => Manifest(id: "builtin.evil")
        };
        var registry = new TrainingProviderRegistry();
        Assert.False(registry.Register(MakeProvider("bad", manifest)).Accepted);
        Assert.True(registry.Register(MakeProvider("good")).Accepted);
        Assert.Single(registry.Failures);
        Assert.Single(registry.Entries);
    }

    [Fact]
    public void MissingLicense_MissingResource_AndChecksumMismatch_AreRejected()
    {
        var registry = new TrainingProviderRegistry();
        Assert.False(registry.Register(MakeProvider("nolicense", license: false)).Accepted);
        const string resources = ",\"resource_manifest\":\"resources.json\"";
        var good = $$"""{"files":[{"path":"model.bin","sha256":"{{Sha("weights")}}"}]}""";
        Assert.True(registry.Register(MakeProvider("res-ok", Manifest(resources: resources), resourcesJson: good)).Accepted);
        Assert.False(registry.Register(MakeProvider("res-missing", Manifest(resources: resources), resourcesJson: good, resourceFileText: null)).Accepted);
        Assert.False(registry.Register(MakeProvider("res-bad", Manifest(resources: resources), resourcesJson: good, resourceFileText: "tampered")).Accepted);
    }

    [Fact]
    public void Provider_IsNotRunnableUntilExplicitlyEnabledWithItsOrigin()
    {
        var registry = new TrainingProviderRegistry();
        var entry = registry.Register(MakeProvider("p")).Entry!;
        Assert.False(entry.Enabled);
        Assert.Throws<InvalidOperationException>(() => registry.Enable("acme.provider", "1.0.0", Path.Combine(_root, "elsewhere")));
        Assert.False(entry.Enabled);
        registry.Enable("acme.provider", "1.0.0", entry.Origin);
        Assert.True(entry.Enabled);
        registry.Disable("acme.provider", "1.0.0");
        Assert.False(entry.Enabled);
    }

    [Fact]
    public async Task Execute_Succeeds_AndReturnsVerifiedArtifacts()
    {
        var (registry, entry) = Enabled();
        var result = await Adapter(registry).ExecuteAsync(Ref(entry), "acme.train", Path.Combine(_root, "job"), new { mode = "ok" }, TestProject.Token);
        Assert.Equal("a.txt", Assert.Single(result.Artifacts).Path);
    }

    [Fact]
    public async Task Execute_RefusesDisabledUndeclaredAndChangedProviders()
    {
        var registry = new TrainingProviderRegistry();
        var entry = registry.Register(MakeProvider("p")).Entry!;
        var adapter = Adapter(registry);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ExecuteAsync(Ref(entry), "acme.train", Path.Combine(_root, "job"), new { }, TestProject.Token));
        registry.Enable("acme.provider", "1.0.0", entry.Origin);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ExecuteAsync(Ref(entry), "acme.other", Path.Combine(_root, "job"), new { }, TestProject.Token));
        File.AppendAllText(Path.Combine(entry.Root, "worker.py"), "\n# tampered\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ExecuteAsync(Ref(entry), "acme.train", Path.Combine(_root, "job"), new { }, TestProject.Token));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("protocol")]
    [InlineData("badhash")]
    [InlineData("unexpected")]
    [InlineData("escape")]
    public async Task Execute_NeverTreatsBrokenResponsesAsSuccess(string mode)
    {
        var (registry, entry) = Enabled();
        await Assert.ThrowsAnyAsync<Exception>(() => Adapter(registry)
            .ExecuteAsync(Ref(entry), "acme.train", Path.Combine(_root, "job-" + mode), new { mode }, TestProject.Token));
    }

    [Fact]
    public async Task Execute_TimesOut_AndKillsTheWorker()
    {
        var (registry, entry) = Enabled();
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<TimeoutException>(() => Adapter(registry, 2)
            .ExecuteAsync(Ref(entry), "acme.train", Path.Combine(_root, "job-hang"), new { mode = "hang" }, TestProject.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void OldJobs_CannotBeRebuiltWithAChangedOrRemovedProvider()
    {
        var (registry, entry) = Enabled();
        var reference = Ref(entry);
        Assert.Same(entry, registry.Resolve(reference.ProviderId!, reference.ProviderVersion!, reference.ProviderHash!));
        Assert.Throws<InvalidOperationException>(() => registry.Resolve(reference.ProviderId!, reference.ProviderVersion!, "other-hash"));
        Assert.Throws<InvalidOperationException>(() =>
            new TrainingProviderRegistry().Resolve(reference.ProviderId!, reference.ProviderVersion!, reference.ProviderHash!));
    }

    [Fact]
    public void ParseRejectsMalformedJson()
    {
        Assert.Throws<InvalidDataException>(() => TrainingProviderValidator.Parse("{not json"));
        Assert.Throws<InvalidDataException>(() => TrainingProviderValidator.Parse("[]"));
        _ = JsonDocument.Parse(Manifest());
    }
}
