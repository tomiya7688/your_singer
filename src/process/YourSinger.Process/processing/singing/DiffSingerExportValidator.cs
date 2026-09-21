namespace YourSinger.Process.Processing.Singing;

public sealed class DiffSingerExportValidator
{
    public IReadOnlyList<string> Validate(string root)
    {
        var errors = new List<string>();

        RequireFile(root, "character.yaml", errors);
        RequireFile(root, "dsconfig.yaml", errors);
        RequireFile(root, "phonemes.txt", errors);
        RequireFile(root, "acoustic.onnx", errors);
        RequireFile(root, Path.Combine("dsdur", "dsconfig.yaml"), errors);
        RequireFile(root, Path.Combine("dsdur", "phonemes.txt"), errors);
        RequireFile(root, Path.Combine("dsdur", "linguistic.onnx"), errors);
        RequireFile(root, Path.Combine("dsdur", "dur.onnx"), errors);
        RequireFile(root, Path.Combine("dsdur", "dsdict.yaml"), errors);
        RequireFile(root, Path.Combine("dsvocoder", "vocoder.yaml"), errors);

        var rootConfig = Path.Combine(root, "dsconfig.yaml");
        if (File.Exists(rootConfig))
        {
            var config = File.ReadAllText(rootConfig);
            foreach (var key in new[] { "phonemes:", "acoustic:", "vocoder:" })
            {
                if (!config.Contains(key, StringComparison.Ordinal))
                    errors.Add($"dsconfig.yamlに必須key '{key.TrimEnd(':')}' がありません。");
            }
        }

        ValidatePredictorDirectory(root, "dspitch", "pitch:", errors);
        ValidatePredictorDirectory(root, "dsvariance", "variance:", errors);

        var phonemes = Path.Combine(root, "phonemes.txt");
        if (File.Exists(phonemes) && File.ReadLines(phonemes).All(string.IsNullOrWhiteSpace))
            errors.Add("phonemes.txtが空です。");

        return errors;
    }

    private static void ValidatePredictorDirectory(
        string root,
        string directoryName,
        string modelKey,
        List<string> errors)
    {
        var directory = Path.Combine(root, directoryName);
        if (!Directory.Exists(directory))
            return;

        RequireFile(root, Path.Combine(directoryName, "dsconfig.yaml"), errors);
        RequireFile(root, Path.Combine(directoryName, "phonemes.txt"), errors);
        RequireFile(root, Path.Combine(directoryName, "linguistic.onnx"), errors);
        RequireFile(root, Path.Combine(directoryName, "dsdict.yaml"), errors);

        var configPath = Path.Combine(directory, "dsconfig.yaml");
        if (File.Exists(configPath))
        {
            var config = File.ReadAllText(configPath);
            if (!config.Contains(modelKey, StringComparison.Ordinal))
                errors.Add($"{directoryName}/dsconfig.yamlに'{modelKey.TrimEnd(':')}'がありません。");
            if (!config.Contains("linguistic:", StringComparison.Ordinal))
                errors.Add($"{directoryName}/dsconfig.yamlに'linguistic'がありません。");
        }
    }

    private static void RequireFile(string root, string relative, List<string> errors)
    {
        if (!File.Exists(Path.Combine(root, relative)))
            errors.Add($"必須ファイルがありません: {relative}");
    }
}
