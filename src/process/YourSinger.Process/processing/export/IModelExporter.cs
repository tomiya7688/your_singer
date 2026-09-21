using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Export;

public interface IModelExporter
{
    ExporterMetadata Metadata { get; }

    Task<ModelExportResult> ExportAsync(
        ProjectWorkspace workspace,
        ModelExportRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ExporterRegistry
{
    private readonly Dictionary<string, IModelExporter> _exporters;

    public ExporterRegistry(IEnumerable<IModelExporter> exporters)
    {
        _exporters = exporters.ToDictionary(
            x => x.Metadata.ExporterId,
            StringComparer.Ordinal);
    }

    public IReadOnlyList<ExporterMetadata> List() =>
        _exporters.Values
            .Select(x => x.Metadata)
            .OrderBy(x => x.Category)
            .ThenBy(x => x.DisplayName, StringComparer.Ordinal)
            .ToArray();

    public IModelExporter GetRequired(string exporterId) =>
        _exporters.TryGetValue(exporterId, out var exporter)
            ? exporter
            : throw new KeyNotFoundException(
                $"Exporterが登録されていません: {exporterId}");
}
