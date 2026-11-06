using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Training;

// {
//   責務: [TrainingProviderValidator: provider.json と resources.json を検証し、Provider内のパスをルート内に閉じ込める]
//   補足: [違反は日本語の原因付き InvalidDataException。未知の項目・未知の権限・未対応のprotocol/schemaは黙って受け入れない]
// }
public static partial class TrainingProviderValidator
{
    [GeneratedRegex(@"^[a-z][a-z0-9_-]*\.[a-z][a-z0-9_.-]*$")]
    private static partial Regex QualifiedIdPattern();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    private static readonly string[] Fields =
    [
        "schema_version", "provider_id", "version", "protocol_version", "display_name_ja", "runtime", "operations",
        "resource_manifest", "permissions_requested", "license_file"
    ];

    private static readonly string[] Required = [.. Fields.Where(x => x != "resource_manifest")];

    // {
    //   責務: [Parse: provider.json の文字列を検証して TrainingProviderDescriptor にする]
    //   エラー: [不正なJSON・schema/protocol版・欠落・未知の項目/権限・絶対パスや .. を含むパスで InvalidDataException]
    // }
    public static TrainingProviderDescriptor Parse(string json)
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
            foreach (var field in Required)
                if (!root.TryGetProperty(field, out _)) throw new InvalidDataException($"必須項目がありません: {field}");
            Require(Text(root, "schema_version", 1, 10), TrainingProviderDescriptor.CurrentSchemaVersion, "schema_version");
            Require(Text(root, "protocol_version", 1, 10), TrainingProviderDescriptor.CurrentProtocolVersion, "protocol_version");
            var id = Text(root, "provider_id", 5, 128);
            if (!QualifiedIdPattern().IsMatch(id)) throw new InvalidDataException($"provider_id の形式が不正です(publisher.name 形式): {id}");
            var version = Text(root, "version", 1, 64);
            if (!VersionPattern().IsMatch(version)) throw new InvalidDataException($"version がSemVer形式ではありません: {version}");
            var runtime = root.GetProperty("runtime");
            if (runtime.ValueKind != JsonValueKind.Object) throw new InvalidDataException("runtime はオブジェクトである必要があります。");
            Closed(runtime, "runtime", ["kind", "entry"]);
            if (Text(runtime, "kind", 1, 40) != "external_process")
                throw new InvalidDataException("runtime.kind は external_process のみ対応しています。");
            return new TrainingProviderDescriptor
            {
                SchemaVersion = TrainingProviderDescriptor.CurrentSchemaVersion, ProviderId = id, Version = version,
                ProtocolVersion = TrainingProviderDescriptor.CurrentProtocolVersion,
                DisplayNameJa = Text(root, "display_name_ja", 1, 100),
                Entry = RelativePath(Text(runtime, "entry", 1, 260), "runtime.entry"),
                Operations = Operations(root.GetProperty("operations")),
                ResourceManifest = root.TryGetProperty("resource_manifest", out _)
                    ? RelativePath(Text(root, "resource_manifest", 1, 260), "resource_manifest") : null,
                PermissionsRequested = Permissions(root.GetProperty("permissions_requested")),
                LicenseFile = RelativePath(Text(root, "license_file", 1, 260), "license_file")
            };
        }
    }

    // {
    //   責務: [VerifyResources: resources.json の各ファイルが実在し、sha256 が一致することを確認する]
    //   引数: [providerRoot: Providerのルート, descriptor: 検証済みの宣言]
    //   戻り値: [files: 検証済みの資源一覧(宣言が無ければ空)]
    //   エラー: [形式不正・パスがルート外・ファイル欠損・hash不一致で InvalidDataException(黙って省略しない)]
    // }
    public static IReadOnlyList<TrainingProviderResourceFile> VerifyResources(string providerRoot, TrainingProviderDescriptor descriptor)
    {
        if (descriptor.ResourceManifest is null) return [];
        var manifestPath = Contain(providerRoot, descriptor.ResourceManifest);
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"資源一覧がありません: {descriptor.ResourceManifest}");
        var files = new List<TrainingProviderResourceFile>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("資源一覧の形式が不正です(files 配列が必要)。");
            foreach (var item in list.EnumerateArray())
            {
                Closed(item, "resources.files", ["path", "sha256"]);
                var path = RelativePath(Text(item, "path", 1, 260), "resources.files.path");
                var sha = Text(item, "sha256", 64, 64);
                if (!Sha256Pattern().IsMatch(sha)) throw new InvalidDataException($"sha256 の形式が不正です: {path}");
                files.Add(new TrainingProviderResourceFile { Path = path, Sha256 = sha.ToLowerInvariant() });
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"資源一覧をJSONとして読めません: {exception.Message}", exception);
        }
        foreach (var file in files)
        {
            var full = Contain(providerRoot, file.Path);
            if (!File.Exists(full)) throw new InvalidDataException($"資源が見つかりません: {file.Path}");
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full)));
            if (actual != file.Sha256) throw new InvalidDataException($"資源のhashが一致しません: {file.Path}");
        }
        return files;
    }

    // {
    //   責務: [Contain: Providerルートからの相対パスを、ルート内の絶対パスにする]
    //   エラー: [ルート外を指す場合に InvalidDataException]
    // }
    public static string Contain(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(relative, rootFull);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Providerのルート外を指すパスです: {relative}");
        return full;
    }

    private static string RelativePath(string value, string label)
    {
        if (Path.IsPathRooted(value) || value.Contains(':') || value.Split('/', '\\').Contains(".."))
            throw new InvalidDataException($"{label} はProviderルートからの相対パスである必要があります(絶対パス・.. は使えません): {value}");
        return value;
    }

    private static void Require(string actual, string expected, string label)
    {
        if (actual != expected) throw new InvalidDataException($"{label} '{actual}' には対応していません(対応: {expected})。");
    }

    private static List<TrainingProviderOperation> Operations(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            throw new InvalidDataException("operations は1件以上の配列である必要があります。");
        var result = new List<TrainingProviderOperation>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("operations の要素はオブジェクトである必要があります。");
            Closed(item, "operations", ["operation_id", "version"]);
            var id = Text(item, "operation_id", 5, 128);
            var version = Text(item, "version", 1, 64);
            if (!QualifiedIdPattern().IsMatch(id)) throw new InvalidDataException($"operation_id の形式が不正です: {id}");
            if (!VersionPattern().IsMatch(version)) throw new InvalidDataException($"operation の version がSemVer形式ではありません: {version}");
            result.Add(new TrainingProviderOperation { OperationId = id, Version = version });
        }
        if (result.Select(x => x.OperationId).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException("operation_id が重複しています。");
        return result;
    }

    private static List<string> Permissions(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException("permissions_requested は配列である必要があります。");
        var values = element.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String
            ? x.GetString()! : throw new InvalidDataException("permissions_requested の要素は文字列である必要があります。")).ToList();
        var unknown = values.Where(x => !TrainingProviderDescriptor.AllowedPermissions.Contains(x)).ToArray();
        if (unknown.Length > 0) throw new InvalidDataException($"未知の権限を要求しています: {string.Join(", ", unknown)}");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count) throw new InvalidDataException("permissions_requested に重複があります。");
        return values;
    }

    private static void Closed(JsonElement item, string name, string[] fields)
    {
        foreach (var property in item.EnumerateObject())
            if (!fields.Contains(property.Name)) throw new InvalidDataException($"{name} に未知の項目があります: {property.Name}");
        foreach (var field in fields)
            if (!item.TryGetProperty(field, out _)) throw new InvalidDataException($"{name} に必須項目がありません: {field}");
    }

    private static string Text(JsonElement root, string name, int min, int max)
    {
        var element = root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{name} は文字列である必要があります。");
        var value = element.GetString()!;
        if (value.Length < min || value.Length > max) throw new InvalidDataException($"{name} の長さが範囲外です({min}〜{max})。");
        return value;
    }
}
