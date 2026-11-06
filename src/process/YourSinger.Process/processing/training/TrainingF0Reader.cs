using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Training;

// 区間のF0特徴(時刻付き配列 [{time_sec, value, confidence}])から有声F0(Hz)を読む。
// 読めない・領域外・不正な場合は空を返し、音域は「測れない」として扱う(捏造しない)。
public static class TrainingF0Reader
{
    public static IReadOnlyList<double> Read(ProjectWorkspace workspace, UniversalVoiceSegment segment)
    {
        if (string.IsNullOrWhiteSpace(segment.F0FeaturePath)) return [];
        try
        {
            var root = Path.GetFullPath(workspace.RootPath);
            var path = Path.GetFullPath(Path.Combine(root, segment.F0FeaturePath));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                return [];
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var values = new List<double>();
            foreach (var frame in document.RootElement.EnumerateArray())
            {
                if (frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var hz) && double.IsFinite(hz) && hz > 0)
                    values.Add(hz);
            }
            return values;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
