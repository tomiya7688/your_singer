using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Talk;

namespace YourSinger.Process.Processing.Export;

public sealed class DiffSingerModelExporter : IModelExporter
{
    private readonly DiffSingerExporter _exporter = new();

    public ExporterMetadata Metadata { get; } = new()
    {
        ExporterId = "openutau-diffsinger",
        DisplayName = "OpenUtau / DiffSinger",
        Version = "1.0.0",
        Category = ExporterCategory.Singing,
        OutputFormat = "openutau-diffsinger",
        TargetRuntime = "OpenUtau",
        TargetRuntimeVersion = DiffSingerExporter.TargetOpenUtauVersion,
        RuntimeControls =
        [
            NumberControl("timbre", "声色", 0, -1, 1),
            NumberControl("formant", "フォルマント", 0, -1, 1),
            NumberControl("breathiness", "息成分", 0, 0, 1)
        ],
        AutomationHooks =
        [
            new AutomationHookDescriptor
            {
                HookId = "note-automation",
                DisplayName = "ノート単位Automation",
                PayloadSchemaVersion = "1"
            }
        ]
    };

    public async Task<ModelExportResult> ExportAsync(
        ProjectWorkspace workspace,
        ModelExportRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureId(request);
        var typed = Deserialize<DiffSingerExportRequest>(request.Options);
        var result = await _exporter.ExportAsync(workspace, typed, cancellationToken);

        return new ModelExportResult
        {
            ExporterId = Metadata.ExporterId,
            ExporterVersion = Metadata.Version,
            OutputDirectory = result.OutputDirectory,
            GeneratedFiles = result.GeneratedFiles
        };
    }

    private void EnsureId(ModelExportRequest request)
    {
        if (!string.Equals(request.ExporterId, Metadata.ExporterId, StringComparison.Ordinal))
            throw new InvalidOperationException("DiffSinger Exporter向けrequestではありません。");
    }

    private static RuntimeControlDescriptor NumberControl(
        string key,
        string name,
        double defaultValue,
        double min,
        double max) => new()
        {
            Key = key,
            DisplayName = name,
            DefaultValue = defaultValue,
            MinValue = min,
            MaxValue = max
        };

    private static T Deserialize<T>(JsonElement element) =>
        JsonSerializer.Deserialize<T>(
            element.GetRawText(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }) ?? throw new InvalidDataException("Exporter optionsを読み取れませんでした。");
}

public sealed class StyleBertVits2ModelExporter : IModelExporter
{
    private readonly StyleBertVits2Exporter _exporter = new();

    public ExporterMetadata Metadata { get; } = new()
    {
        ExporterId = "style-bert-vits2",
        DisplayName = "Style-Bert-VITS2",
        Version = "1.0.0",
        Category = ExporterCategory.Talk,
        OutputFormat = "style-bert-vits2",
        TargetRuntime = "Style-Bert-VITS2",
        TargetRuntimeVersion = StyleBertVits2Exporter.TargetVersion,
        RuntimeControls =
        [
            new RuntimeControlDescriptor
            {
                Key = "style_weight",
                DisplayName = "スタイル強度",
                DefaultValue = 1,
                MinValue = 0,
                MaxValue = 2
            },
            new RuntimeControlDescriptor
            {
                Key = "timbre",
                DisplayName = "声色",
                DefaultValue = 0,
                MinValue = -1,
                MaxValue = 1
            },
            new RuntimeControlDescriptor
            {
                Key = "breathiness",
                DisplayName = "息成分",
                DefaultValue = 0,
                MinValue = 0,
                MaxValue = 1
            }
        ],
        AutomationHooks =
        [
            new AutomationHookDescriptor
            {
                HookId = "utterance-automation",
                DisplayName = "発話単位Automation",
                PayloadSchemaVersion = "1"
            }
        ]
    };

    public async Task<ModelExportResult> ExportAsync(
        ProjectWorkspace workspace,
        ModelExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.ExporterId, Metadata.ExporterId, StringComparison.Ordinal))
            throw new InvalidOperationException("Style-Bert-VITS2 Exporter向けrequestではありません。");

        var typed = JsonSerializer.Deserialize<StyleBertVits2ExportRequest>(
            request.Options.GetRawText(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }) ?? throw new InvalidDataException("Exporter optionsを読み取れませんでした。");

        var result = await _exporter.ExportAsync(workspace, typed, cancellationToken);

        return new ModelExportResult
        {
            ExporterId = Metadata.ExporterId,
            ExporterVersion = Metadata.Version,
            OutputDirectory = result.OutputDirectory,
            GeneratedFiles = result.GeneratedFiles
        };
    }
}
