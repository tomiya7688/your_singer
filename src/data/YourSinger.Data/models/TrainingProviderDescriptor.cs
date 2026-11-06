namespace YourSinger.Data.Models;

// 第三者学習Worker Providerの宣言(provider.json の内容)。機械検証用 schema:
// docs/design/v1-training/schema/training-provider.schema.json。
// 宣言は実在・安全を意味しない: 資源のhash・entry・ライセンスは登録時に実ファイルで検証し、
// 外部コードを含むため、利用者の明示的な有効化がない限り起動しない。OS の sandbox は保証しない。
public sealed class TrainingProviderDescriptor
{
    public const string CurrentSchemaVersion = "1";
    public const string CurrentProtocolVersion = "1";

    public static readonly IReadOnlyList<string> AllowedPermissions = ["read_provided_artifacts", "write_job_artifacts"];

    public required string SchemaVersion { get; init; }
    public required string ProviderId { get; init; }
    public required string Version { get; init; }
    public required string ProtocolVersion { get; init; }
    public required string DisplayNameJa { get; init; }
    // Providerルートからの相対パスの実行ファイル(external_process)。
    public required string Entry { get; init; }
    public required List<TrainingProviderOperation> Operations { get; init; }
    // Providerルートからの相対パス。形式は TrainingProviderResources(resources.json)。無ければ資源なし。
    public string? ResourceManifest { get; init; }
    public required List<string> PermissionsRequested { get; init; }
    public required string LicenseFile { get; init; }
}

public sealed class TrainingProviderOperation
{
    public required string OperationId { get; init; }
    public required string Version { get; init; }
}

// resources.json の形式(要決定の暫定): {"files":[{"path":"相対パス","sha256":"64桁の16進"}]}。
public sealed class TrainingProviderResourceFile
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
}
