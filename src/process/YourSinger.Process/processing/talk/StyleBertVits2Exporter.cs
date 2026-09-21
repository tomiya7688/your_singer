using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Talk;

public sealed class StyleBertVits2Exporter
{
    public const string TargetVersion = "2.7.0";

    public Task<StyleBertVits2ExportResult> ExportAsync(
        ProjectWorkspace workspace,
        StyleBertVits2ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var outputDirectory = Path.Combine(
            workspace.RootPath,
            "exports",
            Sanitize(request.SpeakerId),
            "talk",
            Sanitize(request.ModelName));

        Directory.CreateDirectory(outputDirectory);
        var generated = new List<string>();

        CopyFile(request.ConfigJsonPath, Path.Combine(outputDirectory, "config.json"), generated);
        CopyFile(request.StyleVectorsPath, Path.Combine(outputDirectory, "style_vectors.npy"), generated);

        var weightsName = Path.GetFileName(request.ModelWeightsPath);
        CopyFile(request.ModelWeightsPath, Path.Combine(outputDirectory, weightsName), generated);

        var validator = new StyleBertVits2ExportValidator();
        var errors = validator.Validate(outputDirectory);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Style-Bert-VITS2成果物の検証に失敗しました: " +
                string.Join(" / ", errors));
        }

        return Task.FromResult(new StyleBertVits2ExportResult
        {
            OutputDirectory = outputDirectory,
            TargetVersion = TargetVersion,
            GeneratedFiles = generated
        });
    }

    private static void ValidateRequest(StyleBertVits2ExportRequest request)
    {
        RequireFile(request.ConfigJsonPath, "config.json");
        RequireFile(request.StyleVectorsPath, "style_vectors.npy");
        RequireFile(request.ModelWeightsPath, "model weights");

        if (!string.Equals(
                Path.GetExtension(request.ModelWeightsPath),
                ".safetensors",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "v1ではStyle-Bert-VITS2の学習成果物を.safetensorsで固定します。");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(request.ConfigJsonPath));
        var root = document.RootElement;

        if (!root.TryGetProperty("model_name", out var modelName) ||
            string.IsNullOrWhiteSpace(modelName.GetString()))
            throw new InvalidOperationException("config.jsonにmodel_nameがありません。");

        if (!root.TryGetProperty("version", out var version) ||
            string.IsNullOrWhiteSpace(version.GetString()))
            throw new InvalidOperationException("config.jsonにversionがありません。");
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

    private static string Sanitize(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }
}
