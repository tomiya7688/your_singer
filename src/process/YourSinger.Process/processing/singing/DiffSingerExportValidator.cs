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
        RequireFile(root, Path.Combine("dsdur", "dur.onnx"), errors);

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

        var phonemes = Path.Combine(root, "phonemes.txt");
        if (File.Exists(phonemes) && File.ReadLines(phonemes).All(string.IsNullOrWhiteSpace))
            errors.Add("phonemes.txtが空です。");

        return errors;
    }

    private static void RequireFile(string root, string relative, List<string> errors)
    {
        if (!File.Exists(Path.Combine(root, relative)))
            errors.Add($"必須ファイルがありません: {relative}");
    }
}
