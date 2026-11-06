using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// preset.json の読込と検証(texture-preset.schema.json と同じ契約)。違反は日本語の原因付き InvalidDataException。
public static partial class TexturePresetValidator
{
    [GeneratedRegex(@"^[a-z][a-z0-9_-]*\.[a-z][a-z0-9_.-]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex TagPattern();

    private static readonly string[] Fields =
    [
        "schema_version", "preset_id", "version", "display_name_ja", "description_ja",
        "supported_targets", "traits", "required_capability_tags", "preferred_strategy_tags"
    ];

    // {
    //   責務: [Parse: preset.json の文字列を検証して TexturePresetRecord にする]
    //   処理: [1: JSONとして読む 2: 未知の項目と必須項目を確認する 3: 各値の形式と範囲を確認する]
    //   エラー: [不正なJSON・schema版・欠落・範囲外・未知の項目のとき InvalidDataException]
    // }
    public static TexturePresetRecord Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"JSONとして読めません: {exception.Message}", exception);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("最上位がオブジェクトではありません。");
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name)) throw new InvalidDataException($"未知の項目です: {property.Name}");
            foreach (var field in Fields)
                if (!root.TryGetProperty(field, out _)) throw new InvalidDataException($"必須項目がありません: {field}");

            var schema = Text(root, "schema_version", 1, 10);
            if (schema != TexturePresetRecord.CurrentSchemaVersion)
                throw new InvalidDataException($"schema_version '{schema}' には対応していません(対応: {TexturePresetRecord.CurrentSchemaVersion})。");
            var id = Text(root, "preset_id", 5, 128);
            if (!IdPattern().IsMatch(id)) throw new InvalidDataException($"preset_id の形式が不正です(publisher.name 形式): {id}");
            var version = Text(root, "version", 1, 64);
            if (!VersionPattern().IsMatch(version)) throw new InvalidDataException($"version がSemVer形式ではありません: {version}");
            return new TexturePresetRecord
            {
                SchemaVersion = schema, PresetId = id, Version = version,
                DisplayNameJa = Text(root, "display_name_ja", 1, 100),
                DescriptionJa = Text(root, "description_ja", 1, 1000),
                SupportedTargets = Targets(root.GetProperty("supported_targets")),
                Traits = Traits(root.GetProperty("traits")),
                RequiredCapabilityTags = Tags(root.GetProperty("required_capability_tags"), "required_capability_tags"),
                PreferredStrategyTags = Tags(root.GetProperty("preferred_strategy_tags"), "preferred_strategy_tags")
            };
        }
    }

    // 内容から決まる hash(同じ (id, version) で内容が違うものを検出する)。
    public static string Hash(TexturePresetRecord preset)
    {
        var canonical = string.Join('\n', preset.SchemaVersion, preset.PresetId, preset.Version, preset.DisplayNameJa,
            preset.DescriptionJa, string.Join(',', preset.SupportedTargets.Order(StringComparer.Ordinal)),
            string.Join(',', preset.Traits.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}={x.Value.ToString("R", CultureInfo.InvariantCulture)}")),
            string.Join(',', preset.RequiredCapabilityTags.Order(StringComparer.Ordinal)),
            string.Join(',', preset.PreferredStrategyTags.Order(StringComparer.Ordinal)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // SemVer の大小比較。同じ数値部分では、プレリリースの方が小さい。
    public static int CompareVersions(string left, string right)
    {
        var (a, ap) = Split(left);
        var (b, bp) = Split(right);
        for (var i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        if (ap is null && bp is null) return 0;
        if (ap is null) return 1;
        if (bp is null) return -1;
        return string.CompareOrdinal(ap, bp);
    }

    private static (long[] Numbers, string? Pre) Split(string version)
    {
        var dash = version.IndexOf('-');
        var core = dash < 0 ? version : version[..dash];
        return ([.. core.Split('.').Select(x => long.Parse(x, CultureInfo.InvariantCulture))], dash < 0 ? null : version[(dash + 1)..]);
    }

    private static string Text(JsonElement root, string name, int min, int max)
    {
        var element = root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} は文字列である必要があります。");
        var value = element.GetString()!;
        if (value.Length < min || value.Length > max) throw new InvalidDataException($"{name} の長さが範囲外です({min}〜{max})。");
        return value;
    }

    private static List<string> Targets(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            throw new InvalidDataException("supported_targets は1件以上の配列である必要があります。");
        var values = element.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : string.Empty).ToList();
        if (values.Any(x => !TrainingAssessmentVocabulary.Targets.Contains(x)) || values.Distinct().Count() != values.Count)
            throw new InvalidDataException("supported_targets は talk / singing の重複しない値である必要があります。");
        return values;
    }

    private static Dictionary<string, double> Traits(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("traits はオブジェクトである必要があります。");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!TexturePresetTraits.Names.Contains(property.Name)) throw new InvalidDataException($"未知のtraitです: {property.Name}");
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var value) ||
                !double.IsFinite(value) || value is < 0 or > 1)
                throw new InvalidDataException($"trait {property.Name} は0〜1の数値である必要があります。");
            result[property.Name] = value;
        }
        if (result.Count == 0) throw new InvalidDataException("traits は1件以上必要です。");
        return result;
    }

    private static List<string> Tags(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{name} は配列である必要があります。");
        var values = element.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : string.Empty).ToList();
        if (values.Any(x => !TagPattern().IsMatch(x)) || values.Distinct().Count() != values.Count)
            throw new InvalidDataException($"{name} の値が不正です(小文字英数字と_、重複なし)。");
        return values;
    }
}
