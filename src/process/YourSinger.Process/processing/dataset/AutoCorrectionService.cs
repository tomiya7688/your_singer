using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Dataset;

public sealed class AutoCorrectionService
{
    public const string StageVersion = "v1-08.2";
    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    private readonly AutoCorrectionRepository _correctionRepository;

    public AutoCorrectionService(UniversalVoiceDatasetRepository datasetRepository,
        AutoCorrectionRepository correctionRepository)
    {
        _datasetRepository = datasetRepository;
        _correctionRepository = correctionRepository;
    }

    public async Task<CorrectedDatasetView> BuildAsync(ProjectWorkspace workspace, bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        // 手動編集は補正ON/OFFにかかわらず優先する。観測データの保存ファイルは書き換えない。
        var dataset = await DatasetEditProjection.LoadAsync(workspace, _datasetRepository, cancellationToken);
        var (settings, _) = await _correctionRepository.LoadAsync(workspace, cancellationToken);
        if (enabled.HasValue)
            settings.Enabled = enabled.Value;
        settings.StageVersion = StageVersion;
        var corrections = settings.Enabled ? BuildCorrections(dataset) : BuildObservedOnly(dataset);
        var rejected = corrections.Where(x => x.State == CorrectionState.Rejected)
            .Select(x => x.SegmentId).ToHashSet(StringComparer.Ordinal);
        var fingerprint = DatasetFingerprint.ForView(dataset, settings.Enabled, corrections);
        await _correctionRepository.SaveAsync(workspace, settings, corrections, cancellationToken);
        return new CorrectedDatasetView
        {
            CorrectionEnabled = settings.Enabled,
            Segments = dataset.Segments.Where(x => !rejected.Contains(x.SegmentId))
                .OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToList(),
            AppliedCorrections = corrections,
            Fingerprint = fingerprint
        };
    }

    private static List<CorrectionRecord> BuildObservedOnly(UniversalVoiceDatasetRecord dataset) =>
        dataset.Segments.Select(x => new CorrectionRecord
        {
            SegmentId = x.SegmentId, State = CorrectionState.Observed,
            Method = "observed-only", Confidence = 1.0, Reason = "自動補完・補正OFF"
        }).ToList();

    private static List<CorrectionRecord> BuildCorrections(UniversalVoiceDatasetRecord dataset)
    {
        var result = new List<CorrectionRecord>();
        foreach (var segment in dataset.Segments)
        {
            var state = CorrectionState.Observed;
            var method = "observed";
            var confidence = 1.0;
            string? reason = null;
            if (segment.AsrConfidence < 0.35 || segment.AlignmentConfidence < 0.35)
            {
                state = CorrectionState.Rejected;
                method = "confidence-outlier-rejection";
                confidence = 0.90;
                reason = "音声認識または音素時刻の信頼度が低いため除外";
            }
            else if (segment.PitchReliability < 0.40 && segment.ContentType == SegmentContentType.Singing)
            {
                state = CorrectionState.Corrected;
                method = "pitch-condition-interpolation";
                confidence = 0.65;
                reason = "歌唱の音高信頼度が低いため条件間補間対象";
            }
            else if (segment.AsrConfidence < 0.65 || segment.AlignmentConfidence < 0.65)
            {
                state = CorrectionState.WeakObserved;
                method = "weak-observation-assist";
                confidence = 0.70;
                reason = "観測値を保持しつつ補助推定対象";
            }
            result.Add(new()
            {
                SegmentId = segment.SegmentId, State = state, Method = method,
                Confidence = confidence, Reason = reason
            });
        }
        var counts = dataset.Segments.SelectMany(x => x.Phonemes)
            .GroupBy(x => x.Phoneme, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        foreach (var expected in JapaneseCoverageCatalog.CorePhonemes)
            if (counts.GetValueOrDefault(expected) == 0)
                result.Add(new()
                {
                    SegmentId = "__dataset__", Phoneme = expected, State = CorrectionState.Estimated,
                    Method = "missing-phoneme-estimation", Confidence = 0.45,
                    Reason = "欠損音素のため近接音素・話者特徴・文脈から推定対象"
                });
        foreach (var segment in dataset.Segments)
            foreach (var phoneme in segment.Phonemes)
            {
                if (phoneme.Confidence < 0.35)
                    result.Add(new()
                    {
                        SegmentId = segment.SegmentId, Phoneme = phoneme.Phoneme, State = CorrectionState.Corrected,
                        Method = "phoneme-label-confidence-correction", Confidence = 0.60,
                        OriginalValue = phoneme.Phoneme, CorrectedValue = phoneme.Phoneme,
                        Reason = "音素の信頼度が低いため文脈整合補正対象"
                    });
                else if (counts.GetValueOrDefault(phoneme.Phoneme) < 3)
                    result.Add(new()
                    {
                        SegmentId = segment.SegmentId, Phoneme = phoneme.Phoneme, State = CorrectionState.WeakObserved,
                        Method = "cross-phoneme-speaker-feature-estimation", Confidence = 0.55,
                        Reason = "少量音素のため観測値を保持しつつ他音素から話者特徴を補助推定"
                    });
            }
        return result;
    }
}
