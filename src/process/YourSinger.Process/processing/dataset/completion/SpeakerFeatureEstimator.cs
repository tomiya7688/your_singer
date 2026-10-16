using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset.Completion;

/// <summary>
/// 採用した生成会話の話者特徴を、同一話者の別音素を含む信頼できる観測会話から推定する。
/// 生成区間も推定結果も録音済みの観測とは扱わない。
/// </summary>
public static class SpeakerFeatureEstimator
{
    public const string Method = "cross-phoneme-speaker-feature-estimation";
    private const double MinimumConfidence = 0.65;

    public static CorrectionRecord Apply(
        UniversalVoiceDatasetRecord observedDataset,
        UniversalVoiceSegment target,
        IReadOnlyCollection<string> assistedPhonemes,
        double speakerSimilarity)
    {
        if (!target.SourceId.StartsWith("generated:", StringComparison.Ordinal) ||
            target.ContentType != SegmentContentType.Speech)
            return Deferred(target, "生成された会話区間ではないため、話者特徴を推定しません。");
        if (string.IsNullOrWhiteSpace(target.SpeakerId))
            return Deferred(target, "話者が確定していないため、話者特徴を推定しません。");
        if (target.SpeakerEmbedding.Count > 0)
            return Deferred(target, "既存の話者特徴を自動推定で上書きしません。");
        if (!double.IsFinite(speakerSimilarity) || speakerSimilarity is < 0.80 or > 1)
            return Deferred(target, "生成候補の話者類似度が検証基準を満たしていません。");

        var targets = assistedPhonemes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizePhone)
            .ToHashSet(StringComparer.Ordinal);
        if (targets.Count == 0)
            return Deferred(target, "補助対象の音素がないため話者特徴を推定しません。");

        var references = observedDataset.Segments
            .Where(x =>
                x.SpeakerId == target.SpeakerId &&
                x.ContentType == SegmentContentType.Speech &&
                !x.SourceId.StartsWith("generated:", StringComparison.Ordinal) &&
                x.AsrConfidence >= MinimumConfidence &&
                x.AlignmentConfidence >= MinimumConfidence &&
                x.SpeakerEmbedding.Count > 0 &&
                HasOtherReliablePhone(x, targets))
            .OrderBy(x => x.SegmentId, StringComparer.Ordinal)
            .ToArray();

        if (references.Length == 0)
            return Deferred(target, "補助対象以外の音素を含む信頼できる観測会話がないため、話者特徴を推定しません。");

        var dimensions = references.Select(x => x.SpeakerEmbedding.Count).Distinct().ToArray();
        if (dimensions.Length != 1 || dimensions[0] == 0 ||
            references.Any(x => x.SpeakerEmbedding.Any(value => !double.IsFinite(value))))
            return Deferred(target, "観測話者特徴の次元が一致しないか、不正な数値が含まれています。");

        var values = new double[dimensions[0]];
        foreach (var reference in references)
            for (var i = 0; i < values.Length; i++)
                values[i] += reference.SpeakerEmbedding[i];

        var norm = Math.Sqrt(values.Sum(x => x * x));
        if (!double.IsFinite(norm) || norm <= 1e-12)
            return Deferred(target, "観測話者特徴の平均を正規化できません。");

        target.SpeakerEmbedding.AddRange(values.Select(x => x / norm));
        return new CorrectionRecord
        {
            SegmentId = target.SegmentId,
            SpeakerId = target.SpeakerId,
            State = CorrectionState.Estimated,
            Method = Method,
            ApplicationStatus = CorrectionApplicationStatus.Applied,
            Confidence = speakerSimilarity,
            ResultValues = [.. target.SpeakerEmbedding],
            Reason = $"補助対象以外の音素を含む観測会話{references.Length}件から推定しました。confidenceは生成候補の話者類似度であり、推定精度の確率ではありません。"
        };
    }

    private static bool HasOtherReliablePhone(
        UniversalVoiceSegment segment,
        IReadOnlySet<string> targets) =>
        segment.Phonemes.Any(x =>
            x.Confidence >= MinimumConfidence &&
            !string.IsNullOrWhiteSpace(x.Phoneme) &&
            !targets.Contains(NormalizePhone(x.Phoneme)));

    private static string NormalizePhone(string phoneme) =>
        phoneme is "I" or "U" ? phoneme.ToLowerInvariant() : phoneme;

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
