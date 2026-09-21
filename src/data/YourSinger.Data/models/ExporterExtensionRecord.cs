using System.Text.Json;

namespace YourSinger.Data.Models;

public enum ExporterCategory
{
    Singing,
    Talk,
    Custom
}

public sealed class ExporterMetadata
{
    public required string ExporterId { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required ExporterCategory Category { get; init; }
    public required string OutputFormat { get; init; }
    public string? TargetRuntime { get; init; }
    public string? TargetRuntimeVersion { get; init; }
    public List<RuntimeControlDescriptor> RuntimeControls { get; init; } = [];
    public List<AutomationHookDescriptor> AutomationHooks { get; init; } = [];
}

public sealed class RuntimeControlDescriptor
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public string ValueType { get; init; } = "number";
    public double? DefaultValue { get; init; }
    public double? MinValue { get; init; }
    public double? MaxValue { get; init; }
    public string? Unit { get; init; }
}

public sealed class AutomationHookDescriptor
{
    public required string HookId { get; init; }
    public required string DisplayName { get; init; }
    public string PayloadSchemaVersion { get; init; } = "1";
}

public sealed class ModelExportRequest
{
    public required string ExporterId { get; init; }
    public required string SpeakerId { get; init; }
    public required string ModelName { get; init; }
    public required JsonElement Options { get; init; }
    public Dictionary<string, JsonElement> ExtensionData { get; init; } =
        new(StringComparer.Ordinal);
}

public sealed class ModelExportResult
{
    public required string ExporterId { get; init; }
    public required string ExporterVersion { get; init; }
    public required string OutputDirectory { get; init; }
    public List<string> GeneratedFiles { get; init; } = [];
    public Dictionary<string, JsonElement> ExtensionData { get; init; } =
        new(StringComparer.Ordinal);
}
