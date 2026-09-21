using System.Security.Cryptography;
using System.Text;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Dataset;

public sealed class AutoCorrectionService
{
    public const string StageVersion = "v1-08.1";

    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    private readonly AutoCorrectionRepository _correctionRepository;

    public AutoCorrectionService(
        UniversalVoiceDatasetRepository datasetRepository,
        AutoCorrectionRepository correctionRepository)
    {
        _datasetRepository = datasetRepository;
        _correctionRepository = correctionRepository;
    }

    public async Task<CorrectedDatasetView> BuildAsync(
        ProjectWorkspace workspace,
        bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        var dataset = await _datasetRepository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("Universal Voice Datasetがありません。");

        var (settings, _) = await _correctionRepository.LoadAsync(workspace, cancellationToken);
        if (enabled.HasValue)
            settings.Enabled = enabled.Value;
        settings.StageVersion = StageVersion;

        var corrections = settings.Enabled
            ? BuildCorrections(dataset)
            : BuildObservedOnly(dataset);

        await _correctionRepository.SaveAsync(
            workspace, settings, corrections, cancellationToken);

        return new CorrectedDatasetView
        {
            CorrectionEnabled = settings.Enabled,
            Segments = dataset.Segments
                .Where(x => !corrections.Any(c =>
                    c.SegmentId == x.SegmentId && c.State == CorrectionState.Rejected))
                .ToList(),
            AppliedCorrections = corrections,
            Fingerprint = CreateFingerprint(dataset, settings.Enabled, corrections)
        };
    }

    private static List<CorrectionRecord> BuildObservedOnly(
        UniversalVoiceDatasetRecord dataset) =>
        dataset.Segments.Select(x => new CorrectionRecord
        {
            SegmentId = x.SegmentId,
            State = CorrectionState.Observed,
            Method = "observed-only",
            Confidence = 1.0,
            Reason = "自動補完・補正OFF"
        }).ToList();

    private static List<CorrectionRecord> BuildCorrections(
        UniversalVoiceDatasetRecord dataset)
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
                reason = "ASRまたはalignment confidenceが低いため除外";
            }
            else if (segment.PitchReliability < 0.40 &&
                     segment.ContentType == SegmentContentType.Singing)
            {
                state = CorrectionState.Corrected;
                method = "pitch-condition-interpolation";
                confidence = 0.65;
                reason = "歌唱pitch reliabilityが低いため条件間補間対象";
            }
            else if (segment.AsrConfidence < 0.65 ||
                     segment.AlignmentConfidence < 0.65)
            {
                state = CorrectionState.WeakObserved;
                method = "weak-observation-assist";
                confidence = 0.70;
                reason = "観測値を保持しつつ補助推定対象";
            }

            result.Add(new CorrectionRecord
            {
                SegmentId = segment.SegmentId,
                State = state,
                Method = method,
                Confidence = confidence,
                Reason = reason
            });
        }

        var phonemeCounts = dataset.Segments
            .SelectMany(x => x.Phonemes)
            .GroupBy(x => x.Phoneme, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

        foreach (var segment in dataset.Segments)
        {
            foreach (var phoneme in segment.Phonemes)
            {
                var count = phonemeCounts.GetValueOrDefault(phoneme.Phoneme);
                if (count == 0)
                    continue;

                if (count < 3)
                {
                    result.Add(new CorrectionRecord
                    {
                        SegmentId = segment.SegmentId,
                        Phoneme = phoneme.Phoneme,
                        State = CorrectionState.Estimated,
                        Method = "cross-phoneme-speaker-feature-estimation",
                        Confidence = 0.55,
                        Reason = "少量音素のため他音素から話者特徴を補助推定"
                    });
                }
            }
        }

        return result;
    }

    private static string CreateFingerprint(
        UniversalVoiceDatasetRecord dataset,
        bool enabled,
        IReadOnlyList<CorrectionRecord> corrections)
    {
        var text = string.Join("|",
            dataset.StageVersion,
            enabled,
            string.Join(",", corrections.Select(x =>
                $"{x.SegmentId}:{x.Phoneme}:{x.State}:{x.Method}:{x.Confidence:0.000}")));

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
