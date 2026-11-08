using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// { 責務: [TrainingReferenceAudioServiceTests: 参考音声の同意・形式・長さ・件数・重複・複製先の検査を確認する] }
public sealed class TrainingReferenceAudioServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ys-ref-" + Guid.NewGuid().ToString("N"));

    public TrainingReferenceAudioServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    // 16bit モノラル 16kHz の無音ではない簡単な wav を作る(seed で内容を変える)。
    private string Wav(string name, double seconds, byte seed = 1)
    {
        var path = Path.Combine(_root, name);
        var data = new byte[(int)(seconds * 32000)];
        Array.Fill(data, seed);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + data.Length);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(data.Length);
        writer.Write(data);
        return path;
    }

    private ProjectWorkspace Workspace() => new(Path.Combine(_root, "ws"));

    [Fact]
    public void WithoutConsentNothingIsSaved()
    {
        var workspace = Workspace();
        var exception = Assert.Throws<InvalidOperationException>(
            () => TrainingReferenceAudioService.Import(workspace, "job-1", [Wav("a.wav", 2)], false));
        Assert.Contains("確認が必要", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.RootPath, "training")));
    }

    [Fact]
    public void ValidWavIsCopiedIntoTheJobFolderWithHash()
    {
        var workspace = Workspace();
        var result = TrainingReferenceAudioService.Import(workspace, "job-1", [Wav("a.wav", 2)], true);
        var record = Assert.Single(result.Accepted);
        Assert.Empty(result.Rejected);
        Assert.Equal("a.wav", record.OriginalName);
        Assert.StartsWith("training/references/job-1/", record.RelativePath);
        Assert.Equal(2, record.Seconds, 3);
        Assert.Equal(64, record.Sha256.Length);
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, record.RelativePath)));
    }

    [Fact]
    public void RejectsWithReasonsInsteadOfDroppingSilently()
    {
        var notWav = Path.Combine(_root, "b.mp3");
        File.WriteAllText(notWav, "x");
        var broken = Path.Combine(_root, "c.wav");
        File.WriteAllText(broken, "not a wav");
        var result = TrainingReferenceAudioService.Import(Workspace(), "job-1",
            [notWav, broken, Wav("long.wav", 31), Path.Combine(_root, "missing.wav"), Wav("ok.wav", 3, 5)], true);
        Assert.Equal(["ok.wav"], result.Accepted.Select(x => x.OriginalName));
        var reasons = result.Rejected.ToDictionary(x => x.OriginalName, x => x.Reason);
        Assert.Contains("wav 形式", reasons["b.mp3"]);
        Assert.Contains("読めません", reasons["c.wav"]);
        Assert.Contains("30秒以内", reasons["long.wav"]);
        Assert.Contains("見つかりません", reasons["missing.wav"]);
    }

    [Fact]
    public void DuplicateContentTotalAndCountLimitsAreEnforced()
    {
        var files = Enumerable.Range(0, 10).Select(i => Wav($"f{i}.wav", 10, (byte)(i + 1))).ToList();
        var result = TrainingReferenceAudioService.Import(Workspace(), "job-1", files, true);
        // 10秒×12件=120秒だが件数上限8で止まる。
        Assert.Equal(8, result.Accepted.Count);
        Assert.Equal(2, result.Rejected.Count);
        Assert.All(result.Rejected, x => Assert.Contains("最大8件", x.Reason));

        var same = TrainingReferenceAudioService.Import(Workspace(), "job-2", [Wav("p.wav", 2), Wav("q.wav", 2)], true);
        Assert.Single(same.Accepted);
        Assert.Contains("同じ内容", Assert.Single(same.Rejected).Reason);

        var big = new[] { Wav("g1.wav", 30, 1), Wav("g2.wav", 30, 2), Wav("g3.wav", 30, 3), Wav("g4.wav", 30, 4), Wav("g5.wav", 5, 5) };
        var totals = TrainingReferenceAudioService.Import(Workspace(), "job-3", big, true);
        Assert.Equal(4, totals.Accepted.Count);
        Assert.Contains("合計120秒", Assert.Single(totals.Rejected).Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\b")]
    public void UnsafeJobIdIsRefused(string jobId)
    {
        Assert.Throws<InvalidOperationException>(
            () => TrainingReferenceAudioService.Import(Workspace(), jobId, [Wav("a.wav", 2)], true));
    }
}
