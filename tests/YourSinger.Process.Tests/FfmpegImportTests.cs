using System.Text;
using YourSinger.Data.Models;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Media;
using YourSinger.Process.Processing.Project;

namespace YourSinger.Process.Tests;

public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOURSINGER_TEST_FFMPEG")))
            Skip = "実FFmpeg検証にはYOURSINGER_TEST_FFMPEGの指定が必要です。";
    }
}

// 実FFmpegで入力→WAV保存→再読込を通す。モデル学習のE2Eとは区別する。
public sealed class FfmpegImportTests
{
    private static CancellationToken Token => TestProject.Token;
    private static ProjectImportService Importer() => new(new ProjectRepository(),
        new MediaScanner(new SourceFingerprintService()),
        new FfmpegAudioExtractor(Environment.GetEnvironmentVariable("YOURSINGER_TEST_FFMPEG")));

    [FfmpegFact, Trait("Category", "FfmpegIntegration")]
    public async Task SingleFileImportProducesWavWithoutChangingOriginal()
    {
        using var p = new TestProject();
        var input = Path.Combine(p.Workspace.SourcesPath, "日本語の素材.wav");
        TestProject.WriteWave(input);
        var original = await File.ReadAllBytesAsync(input, Token);
        var manifest = await Importer().CreateOrUpdateAsync(p.Workspace.RootPath, "音声確認", input, cancellationToken: Token);
        var source = Assert.Single(manifest.Sources);
        Assert.Equal(SourceAnalysisState.Ready, source.AnalysisState);
        Assert.NotNull(source.ExtractedAudioPath);
        var output = await File.ReadAllBytesAsync(Path.Combine(p.Workspace.RootPath, source.ExtractedAudioPath), Token);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(output, 0, 4));
        Assert.Equal(original, await File.ReadAllBytesAsync(input, Token));
        Assert.Equal(source.SourceId, Assert.Single((await new ProjectRepository().LoadAsync(p.Workspace, Token))!.Sources).SourceId);
    }

    [FfmpegFact, Trait("Category", "FfmpegIntegration")]
    public async Task FolderImportContinuesAfterCorruptAudio()
    {
        using var p = new TestProject();
        TestProject.WriteWave(Path.Combine(p.Workspace.SourcesPath, "one.wav"));
        TestProject.WriteWave(Path.Combine(p.Workspace.SourcesPath, "nested", "two.wav"));
        var bad = Path.Combine(p.Workspace.SourcesPath, "broken.wav");
        await File.WriteAllTextAsync(bad, "壊れた音声データ", Token);
        var before = await File.ReadAllBytesAsync(bad, Token);
        var manifest = await Importer().CreateOrUpdateAsync(p.Workspace.RootPath,
            "フォルダ確認", p.Workspace.SourcesPath, cancellationToken: Token);
        Assert.Single(manifest.Sources, x => x.AnalysisState == SourceAnalysisState.Failed);
        Assert.Collection(manifest.Sources.Where(x => x.AnalysisState == SourceAnalysisState.Ready),
            x => Assert.NotNull(x.ExtractedAudioPath), x => Assert.NotNull(x.ExtractedAudioPath));
        Assert.Equal(before, await File.ReadAllBytesAsync(bad, Token));
    }

    [FfmpegFact, Trait("Category", "FfmpegIntegration")]
    public async Task UnchangedReimportReusesDecodedAudio()
    {
        using var p = new TestProject();
        var input = Path.Combine(p.Workspace.SourcesPath, "one.wav");
        TestProject.WriteWave(input);
        var importer = Importer();
        var first = await importer.CreateOrUpdateAsync(p.Workspace.RootPath, "再読込", input, cancellationToken: Token);
        var source = Assert.Single(first.Sources);
        var output = Path.Combine(p.Workspace.RootPath, source.ExtractedAudioPath!);
        var marker = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(output, marker);
        var second = await importer.CreateOrUpdateAsync(p.Workspace.RootPath, "再読込", input, cancellationToken: Token);
        Assert.Equal(source.SourceId, Assert.Single(second.Sources).SourceId);
        Assert.Equal(marker, File.GetLastWriteTimeUtc(output));
    }
}
