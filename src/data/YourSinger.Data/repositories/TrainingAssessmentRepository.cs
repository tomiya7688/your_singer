using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Data.Repositories;

// training/assessments/<job-id>/assessment.json の保存・読込。一時ファイル→再読込検証→公開の順で書く。
public sealed class TrainingAssessmentRepository
{
    private const string FileName = "assessment.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    // {
    //   責務: [LoadAsync: 公開済みAssessmentを読み、検証して返す]
    //   処理: [1: job_idをパス要素として検証し領域内の場所を決める 2: 無ければnull 3: 読込・検証]
    //   戻り値: [record: 検証済みrecord。未保存はnull。不正は InvalidDataException]
    // }
    public async Task<TrainingAssessmentRecord?> LoadAsync(
        ProjectWorkspace workspace, string jobId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(workspace, jobId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var record = await ReadAsync(stream, cancellationToken);
        if (record.JobId != jobId)
            throw TrainingAssessmentValidator.Invalid($"job_id '{record.JobId}' が保存先のジョブ '{jobId}' と一致しません。");
        return record;
    }

    // {
    //   責務: [SaveAsync: 検証済みAssessmentを一時ファイル経由で公開する]
    //   処理: [
    //     1: 保存前にrecordを検証する(不正なら何も書かない)
    //     2: 同じディレクトリの一時ファイルへ書き、再読込して検証する
    //     3: 取消を確認してから原子的に置換する。失敗・取消では一時ファイルを消し、公開済みの版を残す
    //   ]
    //   戻り値: [なし]
    // }
    public async Task SaveAsync(
        ProjectWorkspace workspace, TrainingAssessmentRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TrainingAssessmentValidator.Validate(record);
        var path = GetPath(workspace, record.JobId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".assessment-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
            }
            await using (var verify = File.OpenRead(temporaryPath))
            {
                await ReadAsync(verify, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    // {
    //   責務: [Delete: ジョブ作成の失敗時に、そのジョブのAssessment保存先を取り除く]
    //   処理: [1: 保存先を領域内に限って決める 2: あればディレクトリごと削除する]
    // }
    public void Delete(ProjectWorkspace workspace, string jobId)
    {
        var directory = Path.GetDirectoryName(GetPath(workspace, jobId))!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static async Task<TrainingAssessmentRecord> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        TrainingAssessmentRecord? record;
        try
        {
            record = await JsonSerializer.DeserializeAsync<TrainingAssessmentRecord>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw TrainingAssessmentValidator.Invalid(
                $"JSONとして読めません(NaN/Infinity・未知の項目・必須項目の欠落・不正な値を含む可能性があります): {ex.Message}", ex);
        }
        TrainingAssessmentValidator.Validate(record);
        return record!;
    }

    private static string GetPath(ProjectWorkspace workspace, string jobId)
    {
        TrainingAssessmentValidator.RequireSafeComponent(jobId, "job_id");
        var root = Path.GetFullPath(Path.Combine(workspace.TrainingPath, "assessments"));
        var path = Path.GetFullPath(Path.Combine(root, jobId, FileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw TrainingAssessmentValidator.Invalid("保存先がプロジェクトの領域外です。");
        return path;
    }
}
