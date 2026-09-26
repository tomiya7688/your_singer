using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset.Completion;

namespace YourSinger.Process.Processing.Dataset;

public sealed class AutoCorrectionService
{
    public const string StageVersion = "v1-08.6";
    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    private readonly AutoCorrectionRepository _correctionRepository;
    private readonly TranscriptCorrectionService _transcriptCorrection;

    public AutoCorrectionService(
        UniversalVoiceDatasetRepository datasetRepository,
        AutoCorrectionRepository correctionRepository,
        TranscriptCorrectionService? transcriptCorrection = null)
    {
        _datasetRepository = datasetRepository;
        _correctionRepository = correctionRepository;
        _transcriptCorrection = transcriptCorrection ?? new TranscriptCorrectionService();
    }

    public async Task<CorrectedDatasetView> BuildAsync(ProjectWorkspace workspace, bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        var dataset = await new TrainingDatasetSnapshotService(_datasetRepository).LoadAsync(workspace, cancellationToken);
        var (settings, _) = await _correctionRepository.LoadAsync(workspace, cancellationToken);
        if (enabled.HasValue) settings.Enabled = enabled.Value;
        settings.StageVersion = StageVersion;
        if (settings.Enabled) PitchGapInterpolator.ValidateSettings(settings.PitchCompletion);
        var corrections = settings.Enabled ? BuildCorrections(dataset) : BuildObservedOnly(dataset);
        var rejected = corrections.Where(x => x.State == CorrectionState.Rejected && x.ApplicationStatus == CorrectionApplicationStatus.Applied)
            .Select(x => x.SegmentId).ToHashSet(StringComparer.Ordinal);
        if (settings.Enabled)
        {
            corrections.AddRange(await _transcriptCorrection.ApplyAsync(workspace, dataset, cancellationToken));
            AppendPhonemeNeedCorrections(dataset, corrections, rejected);
        }
        if (settings.Enabled && settings.PitchCompletion.Enabled)
        {
            var completer = new PitchFeatureCompletionService();
            foreach (var segment in dataset.Segments.Where(x => !rejected.Contains(x.SegmentId)))
                corrections.AddRange(await completer.ApplyAsync(workspace, segment, settings.PitchCompletion, cancellationToken));
        }
        // 候補の出自検証は自動補正後の投影ではなく、保存済み観測データ＋手動編集を基準にする。
        // 生成自体はここでは行わず、利用者が採用した検証済み会話だけを投影へ追加する。
        if (settings.Enabled)
        {
            var observedForSupplement = await new TrainingDatasetSnapshotService(_datasetRepository)
                .LoadAsync(workspace, cancellationToken);
            corrections.AddRange(await new PhonemeSupplementService()
                .AppendAcceptedAsync(workspace, dataset, observedForSupplement, cancellationToken));
        }
        var fingerprint = TrainingDatasetSnapshotService.CreateFingerprint(dataset, settings.Enabled, corrections, settings.PitchCompletion);
        await _correctionRepository.SaveAsync(workspace, settings, corrections, cancellationToken);
        return new CorrectedDatasetView
        {
            CorrectionEnabled = settings.Enabled,
            Segments = dataset.Segments.Where(x => !rejected.Contains(x.SegmentId)).OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToList(),
            AppliedCorrections = corrections, Fingerprint = fingerprint
        };
    }

    private static List<CorrectionRecord> BuildObservedOnly(UniversalVoiceDatasetRecord dataset) => dataset.Segments.Select(x => new CorrectionRecord
    {
        SegmentId = x.SegmentId, SpeakerId = x.SpeakerId, State = CorrectionState.Observed,
        Method = "observed-only", ApplicationStatus = CorrectionApplicationStatus.NotRequired,
        Confidence = 1, Reason = "自動補完・補正OFF。手動の編集と除外は維持します。"
    }).ToList();

    private static List<CorrectionRecord> BuildCorrections(UniversalVoiceDatasetRecord dataset)
    {
        var result = new List<CorrectionRecord>();
        foreach (var segment in dataset.Segments)
        {
            var rejected = segment.AsrConfidence < 0.35 || segment.AlignmentConfidence < 0.35;
            var weak = segment.AsrConfidence < 0.65 || segment.AlignmentConfidence < 0.65;
            result.Add(new CorrectionRecord
            {
                SegmentId = segment.SegmentId, SpeakerId = segment.SpeakerId,
                State = rejected ? CorrectionState.Rejected : weak ? CorrectionState.WeakObserved : CorrectionState.Observed,
                Method = rejected ? "confidence-outlier-rejection" : "observed",
                ApplicationStatus = rejected ? CorrectionApplicationStatus.Applied : CorrectionApplicationStatus.NotRequired,
                Confidence = Math.Min(segment.AsrConfidence, segment.AlignmentConfidence),
                Reason = rejected ? "文字起こしまたは音素時刻の信頼度が低いため学習対象から除外しました。" : "観測データを保持します。"
            });
        }
        var rejectedIds = result.Where(x => x.State == CorrectionState.Rejected).Select(x => x.SegmentId).ToHashSet(StringComparer.Ordinal);
        return result;
    }

    private static void AppendPhonemeNeedCorrections(
        UniversalVoiceDatasetRecord dataset,
        List<CorrectionRecord> result,
        IReadOnlySet<string> rejectedIds)
    {
        // 文字起こし補正後の音素列で未観測(0回)と少量(1〜2回)を判定する。
        foreach (var speakerId in dataset.Segments.Where(x => x.SpeakerId is not null)
                     .Select(x => x.SpeakerId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!PhonemeSupplementService.References(dataset, speakerId).Any()) continue;
            var counts = PhonemeSupplementService.ReliablePhonemeCounts(dataset, speakerId);
            foreach (var phone in PhonemeSupplementService.SupplementTargets(dataset, speakerId))
            {
                var count = counts.GetValueOrDefault(phone);
                result.Add(new CorrectionRecord
                {
                    SegmentId = "__dataset__", SpeakerId = speakerId, Phoneme = phone,
                    State = count == 0 ? CorrectionState.Estimated : CorrectionState.WeakObserved,
                    Method = count == 0 ? "missing-phoneme-generation" : "sparse-phoneme-generation",
                    ApplicationStatus = CorrectionApplicationStatus.Deferred,
                    Confidence = Math.Min(1, count / (double)PhonemeSupplementService.MinimumObservedCount),
                    OriginalValue = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Reason = count == 0
                        ? "この話者に未観測の音素です。生成データの採用記録は観測とは別に保持します。"
                        : $"この話者では観測{count}回の少量音素です。録音値を保持したまま生成補助を追加できます。"
                });
            }
        }

        foreach (var segment in dataset.Segments.Where(x => !rejectedIds.Contains(x.SegmentId)))
            foreach (var phone in segment.Phonemes.Where(x => x.Confidence < 0.35).Select(x => x.Phoneme).Distinct(StringComparer.Ordinal))
                result.Add(new CorrectionRecord
                {
                    SegmentId = segment.SegmentId, SpeakerId = segment.SpeakerId, Phoneme = phone,
                    State = CorrectionState.WeakObserved, Method = "phoneme-label-review",
                    ApplicationStatus = CorrectionApplicationStatus.Deferred, OriginalValue = phone,
                    Reason = "音素ラベルは低信頼です。自動で修正したことにはせず、元のラベルを保持します。"
                });
    }
}