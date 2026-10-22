using System.Text;
using YourSinger.Data.Models;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Media;
using YourSinger.Process.Processing.Project;

namespace YourSinger.Process.Tests;

public sealed class SingleFileImportFlowTests
{
    private static CancellationToken Token => TestProject.Token;

    // {
    //   責務: [ImportCreatesPersistedAudioAndReusesItOnRetry: 単一ファイル入力から抽出済み素材の保存まで確認する]
    //   処理: [
    //     1: 日本語名のWAVをプロジェクトへ登録する
    //     2: 元素材を保ったまま48 kHz mono PCMへ抽出する
    //     3: 再読込と再実行で保存内容および抽出結果が維持されることを確認する
    //   ]
    //   エラー: [FFmpegの実行またはプロジェクト保存に失敗した場合に失敗する]
    // }
    [Fact]
    public async Task ImportCreatesPersistedAudioAndReusesItOnRetry()
    {
        using var project = new TestProject();
        var sourcePath = Path.Combine(
            Path.GetDirectoryName(project.Workspace.RootPath)!,
            $"single-file-{Guid.NewGuid():N}-素材.wav");

        try
        {
            TestProject.WriteWave(sourcePath);
            var originalBytes = await File.ReadAllBytesAsync(sourcePath, Token);
            var repository = new ProjectRepository();
            var importService = new ProjectImportService(
                repository,
                new MediaScanner(new SourceFingerprintService()),
                new FfmpegAudioExtractor());

            // 単一の日本語パスを通常のプロジェクト登録処理へ渡す。
            var manifest = await importService.CreateOrUpdateAsync(
                project.Workspace.RootPath,
                "単一ファイル入力",
                sourcePath,
                cancellationToken: Token);

            var source = Assert.Single(manifest.Sources);
            Assert.Equal(Path.GetFullPath(sourcePath), source.Path);
            Assert.Equal(MediaType.Audio, source.MediaType);
            Assert.Equal(SourceAnalysisState.Ready, source.AnalysisState);
            Assert.NotNull(source.ExtractedAudioPath);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath, Token));

            // 抽出結果のコンテナとPCM形式が後段入力の契約に一致することを確かめる。
            var extractedPath = Path.GetFullPath(Path.Combine(
                project.Workspace.RootPath,
                source.ExtractedAudioPath));
            Assert.StartsWith(
                Path.GetFullPath(project.Workspace.RootPath) + Path.DirectorySeparatorChar,
                extractedPath,
                StringComparison.OrdinalIgnoreCase);
            AssertWaveFormat(extractedPath);

            // 完了したmanifestを読み直し、同一入力の再実行が抽出成果物を作り直さないことを確認する。
            var savedManifest = await repository.LoadAsync(project.Workspace, Token);
            Assert.NotNull(savedManifest);
            Assert.Equal(source.SourceId, Assert.Single(savedManifest.Sources).SourceId);
            var sentinelTime = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(extractedPath, sentinelTime);

            var retriedManifest = await importService.CreateOrUpdateAsync(
                project.Workspace.RootPath,
                "単一ファイル入力",
                sourcePath,
                cancellationToken: Token);

            Assert.Equal(SourceAnalysisState.Ready, Assert.Single(retriedManifest.Sources).AnalysisState);
            Assert.Equal(sentinelTime, File.GetLastWriteTimeUtc(extractedPath));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath, Token));
        }
        finally
        {
            // テストが作成した元素材だけを片付け、共有データへ触れない。
            File.Delete(sourcePath);
        }
    }

    // {
    //   責務: [FolderImportRegistersNestedAudio: フォルダ入力からネストした音声を登録する]
    //   処理: [1: 日本語名の音声を含むフォルダを登録し、対象外ファイルを除外して全音声を抽出・保存する]
    //   エラー: [FFmpegの実行またはプロジェクト保存に失敗した場合にテストを失敗させる]
    // }
    [Fact]
    public async Task FolderImportRegistersNestedAudio()
    {
        using var project = new TestProject();
        var inputRoot = Path.Combine(project.Workspace.SourcesPath, "取込素材");
        var nestedDirectory = Path.Combine(inputRoot, "ライブ", "二番目");
        Directory.CreateDirectory(nestedDirectory);
        var firstSourcePath = Path.Combine(inputRoot, "一番目.wav");
        var secondSourcePath = Path.Combine(nestedDirectory, "二番目.wav");
        TestProject.WriteWave(firstSourcePath);
        TestProject.WriteWave(secondSourcePath);
        await File.WriteAllTextAsync(Path.Combine(inputRoot, "説明.txt"), "対象外", Token);
        var sourceBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [firstSourcePath] = await File.ReadAllBytesAsync(firstSourcePath, Token),
            [secondSourcePath] = await File.ReadAllBytesAsync(secondSourcePath, Token)
        };
        var repository = new ProjectRepository();
        var importService = new ProjectImportService(
            repository,
            new MediaScanner(new SourceFingerprintService()),
            new FfmpegAudioExtractor());

        // フォルダを通常の登録処理へ渡し、再帰走査からmanifest保存まで確認する。
        var manifest = await importService.CreateOrUpdateAsync(
            project.Workspace.RootPath,
            "フォルダ入力",
            inputRoot,
            cancellationToken: Token);

        Assert.Equal(2, manifest.Sources.Count);
        Assert.Equal(
            sourceBytes.Keys.Order(StringComparer.OrdinalIgnoreCase),
            manifest.Sources.Select(source => source.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(manifest.Sources, source =>
        {
            Assert.Equal(SourceAnalysisState.Ready, source.AnalysisState);
            Assert.NotNull(source.ExtractedAudioPath);
            Assert.True(File.Exists(Path.Combine(project.Workspace.RootPath, source.ExtractedAudioPath)));
        });
        foreach (var (sourcePath, originalBytes) in sourceBytes)
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath, Token));

        var savedManifest = await repository.LoadAsync(project.Workspace, Token);
        Assert.NotNull(savedManifest);
        Assert.Equal(2, savedManifest.Sources.Count);
        Assert.Contains(Path.GetFullPath(inputRoot), savedManifest.InputRoots, StringComparer.OrdinalIgnoreCase);
    }

    // {
    //   責務: [AssertWaveFormat: 抽出音声のRIFFヘッダーとPCM形式を確認する]
    //   処理: [1: WAVヘッダー、mono、48 kHz、16-bit PCMを検証する]
    //   引数: [path: 検証する抽出済みWAV]
    //   エラー: [形式が契約と異なる場合にテストを失敗させる]
    // }
    private static void AssertWaveFormat(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(reader.ReadBytes(4)));
        stream.Position = 8;
        Assert.Equal("WAVE", Encoding.ASCII.GetString(reader.ReadBytes(4)));
        stream.Position = 20;
        Assert.Equal((short)1, reader.ReadInt16());
        Assert.Equal((short)1, reader.ReadInt16());
        Assert.Equal(48_000, reader.ReadInt32());
        stream.Position = 34;
        Assert.Equal((short)16, reader.ReadInt16());
    }
}
