using YourSinger.Data.Models;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Export;
using YourSinger.Process.Processing.Media;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Talk;

namespace YourSinger.Process.Tests;

public sealed class InputAndCoverageTests
{
    private static CancellationToken Token => TestProject.Token;
    private static MediaScanner Scanner() => new(new SourceFingerprintService());

    [Fact]
    public async Task SingleFileScanPreservesOriginalAndJapanesePath()
    {
        using var p = new TestProject();
        var path = Path.Combine(p.Workspace.SourcesPath, "素材.wav");
        TestProject.WriteWave(path);
        var bytes = await File.ReadAllBytesAsync(path, Token);
        var source = Assert.Single(await Scanner().ScanAsync(path, [], "v1", cancellationToken: Token));
        Assert.Equal(MediaType.Audio, source.MediaType);
        Assert.Equal(Path.GetFullPath(path), source.Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token));
        var again = Assert.Single(await Scanner().ScanAsync(path, [source], "v1", cancellationToken: Token));
        Assert.Same(source, again);
    }

    [Fact]
    public async Task FolderScanIncludesNestedMediaButNotUnrelatedFiles()
    {
        using var p = new TestProject();
        TestProject.WriteWave(Path.Combine(p.Workspace.SourcesPath, "音声.WAV"));
        var nested = Path.Combine(p.Workspace.SourcesPath, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "movie.mp4"), "走査だけを検証するダミー", Token);
        await File.WriteAllTextAsync(Path.Combine(nested, "memo.txt"), "対象外", Token);
        var scanned = await Scanner().ScanAsync(p.Workspace.SourcesPath, [], "v1", cancellationToken: Token);
        Assert.Collection(scanned.OrderBy(x => x.MediaType),
            x => Assert.Equal(MediaType.Audio, x.MediaType),
            x => Assert.Equal(MediaType.Video, x.MediaType));
    }

    [Fact]
    public async Task HashDetectsChangedContentWithUnchangedSizeAndTimestamp()
    {
        using var p = new TestProject();
        var path = Path.Combine(p.Workspace.SourcesPath, "sound.wav");
        TestProject.WriteWave(path);
        var initial = Assert.Single(await Scanner().ScanAsync(path, [], "v1", cancellationToken: Token));
        var mtime = File.GetLastWriteTimeUtc(path);
        var bytes = await File.ReadAllBytesAsync(path, Token);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes, Token);
        File.SetLastWriteTimeUtc(path, mtime);
        var changed = Assert.Single(await Scanner().ScanAsync(path, [initial], "v1", cancellationToken: Token));
        Assert.Equal(initial.SourceId, changed.SourceId);
        Assert.Equal(initial.Size, changed.Size);
        Assert.NotEqual(initial.ContentHash, changed.ContentHash);
        Assert.Equal(SourceAnalysisState.Pending, changed.AnalysisState);
    }

    [Fact]
    public async Task AnalysisVersionInvalidatesUnchangedFile()
    {
        using var p = new TestProject();
        var path = Path.Combine(p.Workspace.SourcesPath, "sound.wav");
        TestProject.WriteWave(path);
        var initial = Assert.Single(await Scanner().ScanAsync(path, [], "v1", cancellationToken: Token));
        initial.AnalysisState = SourceAnalysisState.Ready;
        var changed = Assert.Single(await Scanner().ScanAsync(path, [initial], "v2", cancellationToken: Token));
        Assert.NotSame(initial, changed);
        Assert.Equal("v2", changed.AnalysisVersion);
        Assert.Equal(SourceAnalysisState.Pending, changed.AnalysisState);
    }

    [Fact]
    public async Task CanceledSaveDoesNotReplaceExistingManifest()
    {
        using var p = new TestProject();
        var repo = new ProjectRepository();
        var manifest = new ProjectManifest { ProjectId = "p", Name = "日本語プロジェクト", AnalysisVersion = "v1" };
        await repo.SaveAsync(p.Workspace, manifest, Token);
        var before = await File.ReadAllBytesAsync(p.Workspace.ManifestPath, Token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        manifest.Name = "未保存";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.SaveAsync(p.Workspace, manifest, canceled.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(p.Workspace.ManifestPath, Token));
        Assert.Equal("日本語プロジェクト", (await repo.LoadAsync(p.Workspace, Token))!.Name);
    }

    [Fact]
    public async Task MissingPhonemesProduceNonBlockingWarning()
    {
        using var p = new TestProject();
        await p.SeedAsync(p.Segment("a"));
        var diagnostic = await new PhonemeCoverageDiagnosticService(p.DatasetRepository, new PhonemeCoverageRepository())
            .DiagnoseAsync(p.Workspace, Token);
        var global = Assert.Single(diagnostic.Scopes, x => x.SpeakerId is null);
        Assert.Equal(CoverageQuality.Missing, global.Quality);
        Assert.NotEmpty(global.MissingPhonemes);
        var batch = await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk }], Token);
        Assert.Single(batch.Jobs);
    }

    [Fact]
    public async Task AllCatalogPhonemesAtSufficientCountsAreGood()
    {
        using var p = new TestProject();
        var segment = p.Segment("all");
        segment.Phonemes.Clear();
        var all = JapaneseCoverageCatalog.CorePhonemes.Concat(JapaneseCoverageCatalog.Palatalized)
            .Concat(JapaneseCoverageCatalog.Voiced).Concat(JapaneseCoverageCatalog.SemiVoiced)
            .Concat(JapaneseCoverageCatalog.Special).Concat(JapaneseCoverageCatalog.Foreign)
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var phoneme in all)
            for (var i = 0; i < PhonemeCoverageDiagnosticService.LowCountThreshold; i++)
                segment.Phonemes.Add(new() { Phoneme = phoneme, StartSec = i, EndSec = i + 1, Confidence = 0.99 });
        await p.SeedAsync(segment);
        var diagnostic = await new PhonemeCoverageDiagnosticService(p.DatasetRepository, new PhonemeCoverageRepository())
            .DiagnoseAsync(p.Workspace, Token);
        var global = Assert.Single(diagnostic.Scopes, x => x.SpeakerId is null);
        Assert.Equal(CoverageQuality.Good, global.Quality);
        Assert.Empty(global.MissingPhonemes);
        Assert.Empty(global.LowCountPhonemes);
    }

    [Fact]
    public void ExportRegistryExposesBothFormatsAndRejectsUnknown()
    {
        var registry = new ExporterRegistry([new DiffSingerModelExporter(), new StyleBertVits2ModelExporter()]);
        Assert.Contains(registry.List(), x => x.Category == ExporterCategory.Singing);
        Assert.Contains(registry.List(), x => x.Category == ExporterCategory.Talk);
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("unregistered"));
    }

    [Fact]
    public void MissingExportFilesFailValidationForBothFormats()
    {
        using var p = new TestProject();
        Assert.NotEmpty(new DiffSingerExportValidator().Validate(p.Workspace.ExportsPath));
        Assert.NotEmpty(new StyleBertVits2ExportValidator().Validate(p.Workspace.ExportsPath));
    }
}
