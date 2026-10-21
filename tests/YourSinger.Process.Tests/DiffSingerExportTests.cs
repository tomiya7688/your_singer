using YourSinger.Data.Models;
using YourSinger.Process.Processing.Singing;

namespace YourSinger.Process.Tests;

public sealed class DiffSingerExportTests
{
    private static CancellationToken Token => TestProject.Token;

    [Fact]
    public async Task ExportProducesOpenUtauVoicebankStructureAcceptedByValidator()
    {
        using var project = new TestProject();
        var trainerOutput = Path.Combine(project.Workspace.RootPath, "trainer-output");
        var acoustic = WriteFixture(trainerOutput, "acoustic.onnx", "synthetic model fixture");
        var duration = WriteFixture(trainerOutput, "dur.onnx", "synthetic model fixture");
        var linguistic = WriteFixture(trainerOutput, "linguistic.onnx", "synthetic model fixture");
        var dictionary = WriteFixture(trainerOutput, "dsdict.yaml", "a: [a]\n");
        var vocoder = Path.Combine(trainerOutput, "vocoder");
        WriteFixture(vocoder, "vocoder.yaml", "model: vocoder.onnx\n");
        WriteFixture(vocoder, "vocoder.onnx", "synthetic model fixture");

        var result = await new DiffSingerExporter().ExportAsync(project.Workspace, new DiffSingerExportRequest
        {
            SingerName = "テスト歌手",
            SpeakerId = "spk_a",
            AcousticModelPath = acoustic,
            DurationModelPath = duration,
            DurationLinguisticModelPath = linguistic,
            DurationDictionaryPath = dictionary,
            VocoderDirectory = vocoder,
            Phonemes = ["a", "k"]
        }, Token);

        Assert.Equal(DiffSingerExporter.TargetOpenUtauVersion, result.OpenUtauVersion);
        Assert.Empty(new DiffSingerExportValidator().Validate(result.OutputDirectory));
        Assert.All(new[]
        {
            "character.yaml",
            "dsconfig.yaml",
            "phonemes.txt",
            "acoustic.onnx",
            Path.Combine("dsdur", "dsconfig.yaml"),
            Path.Combine("dsdur", "phonemes.txt"),
            Path.Combine("dsdur", "dsdict.yaml"),
            Path.Combine("dsdur", "linguistic.onnx"),
            Path.Combine("dsdur", "dur.onnx"),
            Path.Combine("dsvocoder", "vocoder.yaml"),
            Path.Combine("dsvocoder", "vocoder.onnx")
        }, relative => Assert.True(File.Exists(Path.Combine(result.OutputDirectory, relative)), relative));
    }

    private static string WriteFixture(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
}
