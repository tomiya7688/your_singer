using System.Text;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Talk;

// {
//   責務: [StyleBertVits2DatasetBuilder: 確定済みのトーク学習入力を公式4列形式へ変換する]
//   フィールド: [datasetRepository: 解析済み区間と学習計画の照合元]
// }
public sealed class StyleBertVits2DatasetBuilder(UniversalVoiceDatasetRepository datasetRepository)
{
    public const string StageVersion = "v1-11.3";

    // {
    //   責務: [BuildAsync: 対象話者の音声と書き起こしを学習ジョブ内へ保存する]
    //   処理: [1: 学習計画を照合する 2: 音声と4列リストを複製する 3: 完成時に入力を公開する]
    //   エラー: [入力変更・列として不正な文字・音声不足の場合に失敗する]
    // }
    public async Task<StyleBertVits2DatasetRecord> BuildAsync(ProjectWorkspace workspace, TrainingJobRecord job,
        string speakerName, CancellationToken cancellationToken = default)
    {
        if (job.Target != TrainingTarget.Talk)
            throw new InvalidOperationException("トーク学習ジョブのみトーク用データを生成できます。");
        RequireField(speakerName);
        var selected = await TrainingDatasetSnapshotService.ResolveJobAsync(datasetRepository, workspace, job, cancellationToken);
        var root = Path.Combine(workspace.RootPath, "training", "style-bert-vits2", TrainingInputDirectory.RequireComponent(job.JobId));
        return await TrainingInputDirectory.BuildAsync(root, async staging =>
        {
            var rawDirectory = Path.Combine(staging, "raw");
            Directory.CreateDirectory(rawDirectory);
            var items = new List<StyleBertVits2TrainingItem>();
            var lines = new List<string>();
            foreach (var segment in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireField(segment.Transcript);
                var source = Path.GetFullPath(segment.AudioPath, workspace.RootPath);
                var fileName = TrainingInputDirectory.RequireComponent(segment.SegmentId) + ".wav";
                File.Copy(source, Path.Combine(rawDirectory, fileName), overwrite: false);
                // 公式の--correct_pathはwavs/を付けるため、raw/からの相対名を渡す。
                lines.Add($"{fileName}|{speakerName}|JP|{segment.Transcript}");
                items.Add(new StyleBertVits2TrainingItem
                {
                    SegmentId = segment.SegmentId, AudioPath = Path.Combine(root, "raw", fileName),
                    Transcript = segment.Transcript, Phonemes = segment.Phonemes.Select(x => x.Phoneme).ToList(),
                    SpeakerName = speakerName,
                    StyleProsody = new StyleProsodyRecord
                    {
                        SpeakingRate = segment.StyleProsody.SpeakingRate,
                        PitchRangeSemitones = segment.StyleProsody.PitchRangeSemitones,
                        EnergyVariation = segment.StyleProsody.EnergyVariation, PauseRatio = segment.StyleProsody.PauseRatio
                    }
                });
            }
            await File.WriteAllLinesAsync(Path.Combine(staging, "esd.list"), lines, new UTF8Encoding(false), cancellationToken);
            var record = new StyleBertVits2DatasetRecord
            {
                StageVersion = StageVersion, JobId = job.JobId, SpeakerId = job.SpeakerId,
                DatasetFingerprint = job.DatasetFingerprint, AutoCorrectionEnabled = job.AutoCorrectionEnabled, Items = items
            };
            await using var stream = File.Create(Path.Combine(staging, "dataset.json"));
            await JsonSerializer.SerializeAsync(stream, record,
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }, cancellationToken);
            return record;
        }, cancellationToken);
    }

    // { 責務: [RequireField: 記録内容を変えずに4列形式へ保存できる文字列を確認する] }
    private static void RequireField(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['|', '\r', '\n']) >= 0)
            throw new InvalidDataException("トーク学習の話者名と書き起こしには空文字・縦線・改行を使用できません。");
    }
}
