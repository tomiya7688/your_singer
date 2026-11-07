using System.Security.Cryptography;
using System.Text;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// { 責務: [TalkCheckpointInventoryTests: 期待hashと実測hashの照合・欠落・不正なlockの扱いを確認する] }
public sealed class TalkCheckpointInventoryTests
{
    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<(string Root, string Lock)> ArrangeAsync(string dir, bool dropOne = false, string? wrongContent = null)
    {
        Directory.CreateDirectory(Path.Combine(dir, "pretrained"));
        foreach (var name in TalkCheckpointInventory.Names.Skip(dropOne ? 1 : 0))
            await File.WriteAllTextAsync(Path.Combine(dir, "pretrained", name), wrongContent ?? name, TestProject.Token);
        var files = string.Join(",", TalkCheckpointInventory.Names.Select(n => $"{{\"path\":\"{n}\",\"sha256\":\"{Sha(n)}\"}}"));
        var lockPath = Path.Combine(dir, "lock.json");
        await File.WriteAllTextAsync(lockPath,
            $"{{\"source_revision\":\"rev1\",\"resources\":[{{\"name\":\"pretrained\",\"files\":[{files}]}}]}}", TestProject.Token);
        return (dir, lockPath);
    }

    [Fact]
    public async Task MatchingFilesGiveExistingCheckpointsWithEqualHashes()
    {
        using var project = new TestProject();
        var (root, lockPath) = await ArrangeAsync(project.Workspace.RootPath);
        var list = await TalkCheckpointInventory.ScanAsync(root, lockPath, TestProject.Token);
        Assert.Equal(3, list.Count);
        Assert.All(list, c => { Assert.True(c.Exists); Assert.Equal(c.ExpectedSha256, c.ActualSha256); Assert.Equal("rev1", c.BackendRevision); });
    }

    [Fact]
    public async Task MissingAndTamperedFilesAreVisibleToThePreflight()
    {
        using var project = new TestProject();
        var (root, lockPath) = await ArrangeAsync(project.Workspace.RootPath, dropOne: true, wrongContent: "tampered");
        var list = await TalkCheckpointInventory.ScanAsync(root, lockPath, TestProject.Token);
        Assert.False(list[0].Exists);
        Assert.Null(list[0].ActualSha256);
        Assert.NotEqual(list[1].ExpectedSha256, list[1].ActualSha256);
    }

    [Fact]
    public async Task ALockWithoutThePretrainedResourceIsAnErrorNotAnEmptyInventory()
    {
        using var project = new TestProject();
        var lockPath = Path.Combine(project.Workspace.RootPath, "bad.json");
        await File.WriteAllTextAsync(lockPath, "{\"source_revision\":\"r\",\"resources\":[]}", TestProject.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => TalkCheckpointInventory.ScanAsync(project.Workspace.RootPath, lockPath, TestProject.Token));
    }
}
