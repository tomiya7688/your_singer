using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;

namespace YourSinger.Process.Processing.Training;

/// <summary>ジョブ計画後の編集を黙って混ぜず、確定した学習入力と照合する。</summary>
public static class TrainingDatasetResolver
{
    public static async Task<IReadOnlyList<UniversalVoiceSegment>> ResolveAsync(ProjectWorkspace workspace,
        UniversalVoiceDatasetRepository repository, TrainingJobRecord job, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (job.Target is not (TrainingTarget.Talk or TrainingTarget.Singing))
            throw new InvalidOperationException("学習ジョブの用途が不正です。");
        if (string.IsNullOrWhiteSpace(job.JobId) || job.JobId is "." or ".." ||
            job.JobId.IndexOfAny(['/', '\\', ':']) >= 0 || job.JobId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("学習ジョブIDをフォルダ名として使用できません。");
        var view = await new AutoCorrectionService(repository, new AutoCorrectionRepository())
            .BuildAsync(workspace, job.AutoCorrectionEnabled, cancellationToken);
        var ids = job.SegmentIds.ToHashSet(StringComparer.Ordinal);
        var segments = view.Segments.Where(x => ids.Contains(x.SegmentId) && x.SpeakerId == job.SpeakerId &&
            TrainingJobPlanningService.MatchesTarget(x, job.Target)).OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToArray();
        var fingerprint = DatasetFingerprint.ForJob(view.Fingerprint, job.SpeakerId, job.Target, segments.Select(x => x.SegmentId));
        if (ids.Count == 0 || ids.Count != job.SegmentIds.Count || segments.Length != ids.Count ||
            !string.Equals(fingerprint, job.DatasetFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("学習入力がジョブ作成時から変更されたか、旧形式のジョブです。学習ジョブを作り直してください。");
        return segments;
    }
}
