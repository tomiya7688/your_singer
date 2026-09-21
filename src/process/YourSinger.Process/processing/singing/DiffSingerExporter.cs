using System.Text;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Singing;

public sealed class DiffSingerExporter
{
    public const string TargetOpenUtauVersion = "0.1.565";

    public Task<DiffSingerExportResult> ExportAsync(
        ProjectWorkspace workspace,
        DiffSingerExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var outputDirectory = Path.Combine(
            workspace.RootPath,
            "exports",
            Sanitize(request.SpeakerId),
            "singing",
            Sanitize(request.SingerName));

        Directory.CreateDirectory(outputDirectory);

        var generated = new List<string>();
        CopyFile(request.AcousticModelPath, Path.Combine(outputDirectory, "acoustic.onnx"), generated);
        CopyFile(request.DurationModelPath, Path.Combine(outputDirectory, "dsdur", "dur.onnx"), generated);
        CopyFile(request.DurationLinguisticModelPath, Path.Combine(outputDirectory, "dsdur", "linguistic.onnx"), generated);
        CopyFile(request.DurationDictionaryPath, Path.Combine(outputDirectory, "dsdur", "dsdict.yaml"), generated);

        if (!string.IsNullOrWhiteSpace(request.PitchDirectory))
            CopyDirectory(request.PitchDirectory, Path.Combine(outputDirectory, "dspitch"), generated);

        if (!string.IsNullOrWhiteSpace(request.VarianceDirectory))
            CopyDirectory(request.VarianceDirectory, Path.Combine(outputDirectory, "dsvariance"), generated);

        CopyDirectory(request.VocoderDirectory, Path.Combine(outputDirectory, "dsvocoder"), generated);

        var phonemes = request.Phonemes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        WriteText(
            Path.Combine(outputDirectory, "phonemes.txt"),
            string.Join(Environment.NewLine, phonemes) + Environment.NewLine,
            generated);

        WriteText(
            Path.Combine(outputDirectory, "character.yaml"),
            $"name: {YamlScalar(request.SingerName)}{Environment.NewLine}" +
            $"singer_type: diffsinger{Environment.NewLine}" +
            $"version: your_singer-v1{Environment.NewLine}",
            generated);

        WriteText(
            Path.Combine(outputDirectory, "dsconfig.yaml"),
            "phonemes: phonemes.txt\n" +
            "acoustic: acoustic.onnx\n" +
            "vocoder: dsvocoder\n" +
            "predict_dur: true\n",
            generated);

        var durationDirectory = Path.Combine(outputDirectory, "dsdur");
        Directory.CreateDirectory(durationDirectory);
        WriteText(
            Path.Combine(durationDirectory, "phonemes.txt"),
            string.Join(Environment.NewLine, phonemes) + Environment.NewLine,
            generated);
        WriteText(
            Path.Combine(durationDirectory, "dsconfig.yaml"),
            "phonemes: phonemes.txt\n" +
            "linguistic: linguistic.onnx\n" +
            "dur: dur.onnx\n" +
            "predict_dur: true\n",
            generated);

        var result = new DiffSingerExportResult
        {
            OutputDirectory = outputDirectory,
            OpenUtauVersion = TargetOpenUtauVersion,
            GeneratedFiles = generated
        };
        return Task.FromResult(result);
    }

    private static void ValidateRequest(DiffSingerExportRequest request)
    {
        RequireFile(request.AcousticModelPath, "acoustic model");
        RequireFile(request.DurationModelPath, "duration model");
        RequireFile(request.DurationLinguisticModelPath, "duration linguistic model");
        RequireFile(request.DurationDictionaryPath, "duration dictionary");

        ValidateOptionalPredictorDirectory(request.PitchDirectory, "dspitch");
        ValidateOptionalPredictorDirectory(request.VarianceDirectory, "dsvariance");

        if (!Directory.Exists(request.VocoderDirectory))
            throw new DirectoryNotFoundException($"vocoder directoryがありません: {request.VocoderDirectory}");
        RequireFile(Path.Combine(request.VocoderDirectory, "vocoder.yaml"), "vocoder config");

        if (request.Phonemes.Count == 0)
            throw new InvalidOperationException("phoneme定義がありません。");
    }

    private static void ValidateOptionalPredictorDirectory(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"{label} directoryがありません: {path}");

        RequireFile(Path.Combine(path, "dsconfig.yaml"), $"{label} config");
        RequireFile(Path.Combine(path, "phonemes.txt"), $"{label} phoneme definition");
        RequireFile(Path.Combine(path, "linguistic.onnx"), $"{label} linguistic model");
        RequireFile(Path.Combine(path, "dsdict.yaml"), $"{label} dictionary");
    }

    private static void RequireFile(string path, string label)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{label}がありません。", path);
    }

    private static void CopyFile(string source, string destination, List<string> generated)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        generated.Add(destination);
    }

    private static void CopyDirectory(string source, string destination, List<string> generated)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            generated.Add(target);
        }
    }

    private static void WriteText(string path, string content, List<string> generated)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        generated.Add(path);
    }

    private static string Sanitize(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }

    private static string YamlScalar(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
