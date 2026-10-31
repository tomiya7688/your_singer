using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Processing.Singing;

// { 責務: [DiffSingerAcousticDatasetValidator: 音響入力生成直前に保存済み項目と測定済みsegmentを照合する] }
internal static class DiffSingerAcousticDatasetValidator
{
    // { 責務: [ValidateAsync: jobに対応する現在のsegmentと保存済みdatasetを比較する] }
    internal static async Task<string> ValidateAsync(ProjectWorkspace workspace, TrainingJobRecord job, CancellationToken token)
    {
        var selected = await TrainingDatasetSnapshotService.ResolveJobAsync(new UniversalVoiceDatasetRepository(), workspace, job, token);
        var path = Path.Combine("training", "diffsinger", TrainingInputDirectory.RequireComponent(job.JobId), "dataset.json");
        var bytes = await MfaMeasurementFiles.ReadSingingDatasetAsync(workspace, path, token);
        DiffSingerDatasetRecord? document;
        try
        {
            document = JsonSerializer.Deserialize<DiffSingerDatasetRecord>(bytes,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? throw new InvalidDataException("歌唱学習入力が空です。");
        }
        catch (JsonException exception) { throw new InvalidDataException("歌唱学習入力の形式が不正です。再生成してください。", exception); }
        if (document.StageVersion != DiffSingerDatasetBuilder.StageVersion || document.JobId != job.JobId ||
            document.SpeakerId != job.SpeakerId || document.DatasetFingerprint != job.DatasetFingerprint ||
            document.AutoCorrectionEnabled != job.AutoCorrectionEnabled || document.Items is null || document.Items.Count != selected.Count)
            throw new InvalidDataException("歌唱学習入力と現在のjobが一致しません。再生成してください。");
        for (var i = 0; i < selected.Count; i++)
        {
            var segment = selected[i];
            var item = document.Items[i];
            await MfaMeasurementValidator.ValidateFeatureAsync(workspace, segment, token);
            var reference = segment.AlignmentMeasurement;
            if (item is null || item.Phonemes is null || item.PhonemeStartsSec is null ||
                item.PhonemeEndsSec is null || item.DurationsSec is null)
                throw new InvalidDataException("歌唱学習項目が欠けています。再生成してください。");
            var actual = item.AlignmentMeasurement;
            if (!segment.HasValidSingingTrainingAlignment() || reference is null || actual is null ||
                reference.Path != actual.Path || reference.Sha256 != actual.Sha256 || reference.AudioSha256 != actual.AudioSha256 ||
                item.SegmentId != segment.SegmentId || item.AudioPath != Path.GetFullPath(segment.AudioPath, workspace.RootPath) ||
                item.Transcript != segment.Transcript || item.AlignmentMethod != segment.AlignmentMethod ||
                item.F0FeaturePath != Path.GetFullPath(segment.F0FeaturePath, workspace.RootPath) ||
                item.EnergyFeaturePath != Path.GetFullPath(segment.EnergyFeaturePath, workspace.RootPath) ||
                item.MeanF0Hz != segment.MeanF0Hz || item.PitchReliability != segment.PitchReliability ||
                !item.Phonemes.SequenceEqual(segment.Phonemes.Select(x => x.Phoneme)) ||
                !item.PhonemeStartsSec.SequenceEqual(segment.Phonemes.Select(x => x.StartSec)) ||
                !item.PhonemeEndsSec.SequenceEqual(segment.Phonemes.Select(x => x.EndSec)) ||
                !item.DurationsSec.SequenceEqual(segment.Phonemes.Select(x => Math.Max(0, x.EndSec - x.StartSec))))
                throw new InvalidDataException("歌唱学習項目と測定記録が一致しません。再生成してください。");
        }
        return MfaMeasurementFiles.Hash(bytes);
    }
}