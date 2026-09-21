using System.Text.Json;

namespace YourSinger.Process.Processing.Talk;

public sealed class StyleBertVits2ExportValidator
{
    public IReadOnlyList<string> Validate(string root)
    {
        var errors = new List<string>();

        var configPath = Path.Combine(root, "config.json");
        var styleVectorsPath = Path.Combine(root, "style_vectors.npy");

        if (!File.Exists(configPath))
            errors.Add("config.jsonがありません。");
        if (!File.Exists(styleVectorsPath))
            errors.Add("style_vectors.npyがありません。");

        var weights = Directory.Exists(root)
            ? Directory.GetFiles(root, "*.safetensors", SearchOption.TopDirectoryOnly)
            : [];
        if (weights.Length == 0)
            errors.Add(".safetensorsモデルがありません。");

        if (!File.Exists(configPath))
            return errors;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var config = document.RootElement;

            RequireString(config, "model_name", "config.json", errors);
            RequireString(config, "version", "config.json", errors);

            if (!config.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                errors.Add("config.jsonにdata objectがありません。");
                return errors;
            }

            RequireNumber(data, "n_speakers", "data", errors);
            RequireNumber(data, "num_styles", "data", errors);
            RequireObject(data, "spk2id", "data", errors);
            RequireObject(data, "style2id", "data", errors);

            if (data.TryGetProperty("spk2id", out var spk2id) &&
                spk2id.ValueKind == JsonValueKind.Object &&
                !spk2id.EnumerateObject().Any())
                errors.Add("data.spk2idが空です。");

            if (data.TryGetProperty("style2id", out var style2id) &&
                style2id.ValueKind == JsonValueKind.Object &&
                !style2id.EnumerateObject().Any())
                errors.Add("data.style2idが空です。");
        }
        catch (JsonException ex)
        {
            errors.Add($"config.jsonを解析できません: {ex.Message}");
        }

        return errors;
    }

    private static void RequireString(
        JsonElement element,
        string key,
        string parent,
        List<string> errors)
    {
        if (!element.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            errors.Add($"{parent}.{key}がありません。");
    }

    private static void RequireNumber(
        JsonElement element,
        string key,
        string parent,
        List<string> errors)
    {
        if (!element.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.Number)
            errors.Add($"{parent}.{key}がありません。");
    }

    private static void RequireObject(
        JsonElement element,
        string key,
        string parent,
        List<string> errors)
    {
        if (!element.TryGetProperty(key, out var value) ||
            value.ValueKind != JsonValueKind.Object)
            errors.Add($"{parent}.{key}がありません。");
    }
}
