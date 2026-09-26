using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset.Completion;

/// <summary>
/// 生成・低信頼区間へ、同一話者の別音素を含む観測区間から話者特徴を推定する。
/// 推定値を観測済み特徴として扱わない。
/// </summary>
public static class SpeakerFeatureEstimator
{
    public const string Method = "cross-phoneme-speaker-feature-estimation";

    public static CorrectionRecord Apply(
        UniversalVoiceDatasetRecord observedDataset,
        UniversalVoiceSegment target,
        IReadOnlyCollection<string> targetPhonemes,
        double verificationConfidence)
    {
        if (target.SpeakerId is null)
            return Deferred(target, "話者が確定していないため話者特徴を推定しません。");

        var targets = targetPhonemes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizePhone)
            .ToHashSet(StringComparer.Ordinal);

        var references = observedDataset.Segments
            .Where(x =>
                x.SpeakerId == target.SpeakerId &&
                !x.SourceId.StartsWith("generated:", StringComparison.Ordinal) &&
                x.AsrConfidence >= 0.65 &&
                x.AlignmentConfidence >= 0.65 &&
                x.SpeakerEmbedding.Count > 0 &&
                HasOtherReliablePhone(x, targets))
            .OrderBy(x => x.SegmentId, StringComparer.Ordinal)
            .ToArray();

        if (references.Length == 0)
            return Deferred(target, "補助対象以外の音素を含む信頼できる観測区間がないため、話者特徴を推定しません。");

        var dimensions = references.Select(x => x.SpeakerEmbedding.Count).Distinct().ToArray();
        if (dimensions.Length != 1 || dimensions[0] <= 0 ||
            references.Any(x => x.SpeakerEmbedding.Any(value => !double.IsFinite(value))))
            return Deferred(target, "観測話者特徴の次元または数値が一致しないため、話者特徴を推定しません。");

        var estimate = new double[dimensions[0]];
        foreach (var reference in references)
            for (var i = 0; i < estimate.Length; i++)
                estimate[i] += reference.SpeakerEmbedding[i];

        var norm = Math.Sqrt(estimate.Sum(x => x * x));
        if (!double.IsFinite(norm) || norm <= 1e-12)
            return Deferred(target, "観測話者特徴の平均が無効なため、話者特徴を推定しません。");

        target.SpeakerEmbedding.Clear();
        target.SpeakerEmbedding.AddRange(estimate.Select(x => x / norm));

        var confidence = double.IsFinite(verificationConfidence)
            ? Math.Clamp(verificationConfidence, 0, 1)
            : 0;

        return new CorrectionRecord
        {
            SegmentId = target.SegmentId,
            SpeakerId = target.SpeakerId,
            State = CorrectionState.Estimated,
            Method = Method,
            ApplicationStatus = CorrectionApplicationStatus.Applied,
            Confidence = confidence,
            ResultValues = [.. target.SpeakerEmbedding],
            Reason = $"補助対象以外の音素を含む観測区間{references.Length}件の話者特徴を平均・正規化して推定しました。生成音声からの観測値ではありません。"
        };
    }

    private static bool HasOtherReliablePhone(
        UniversalVoiceSegment segment,
        IReadOnlySet<string> targets) =>
        segment.Phonemes.Any(x =>
            x.Confidence >= 0.65 &&
            !string.IsNullOrWhiteSpace(x.Phoneme) &&
            !targets.Contains(NormalizePhone(x.Phoneme)));

    private static string NormalizePhone(string value) =>
        value is "I" or "U" ? value.ToLowerInvariant() : value;

    private static CorrectionRecord Deferred(UniversalVoiceSegment target, string reason) => new()
    {
        SegmentId = target.SegmentId,
        SpeakerId = target.SpeakerId,
        State = CorrectionState.Estimated,
        Method = Method,
        ApplicationStatus = CorrectionApplicationStatus.Deferred,
        Reason = reason
    };
}
