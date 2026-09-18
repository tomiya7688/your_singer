namespace YourSinger.Data.Processing;

public sealed class ProjectWorkspace
{
    public ProjectWorkspace(string rootPath) => RootPath = Path.GetFullPath(rootPath);

    public string RootPath { get; }
    public string ManifestPath => Path.Combine(RootPath, "project.json");
    public string SourcesPath => Path.Combine(RootPath, "sources");
    public string ExtractedPath => Path.Combine(RootPath, "extracted");
    public string SeparatedPath => Path.Combine(RootPath, "separated");
    public string CleanedPath => Path.Combine(RootPath, "cleaned");
    public string SegmentsPath => Path.Combine(RootPath, "segments");
    public string RejectedPath => Path.Combine(RootPath, "rejected");
    public string MetadataPath => Path.Combine(RootPath, "metadata");
    public string FeaturesPath => Path.Combine(RootPath, "features");
    public string CachePath => Path.Combine(RootPath, "cache");
    public string ExportsPath => Path.Combine(RootPath, "exports");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        foreach (var directory in new[]
        {
            SourcesPath, ExtractedPath, SeparatedPath, CleanedPath, SegmentsPath,
            RejectedPath, MetadataPath, FeaturesPath, CachePath, ExportsPath
        })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
