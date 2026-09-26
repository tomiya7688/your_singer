using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Dataset.Completion;

public sealed class TranscriptCorrectionService
{
    public const string ReviewVersion = "transcript-consensus-1";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly Func<string, object, CancellationToken, Task<JsonElement>> _send;
    private readonly Func<bool> _runtimeAvailable;

    public TranscriptCorrectionService(
        Func<string, object, CancellationToken, Task<JsonElement>>? send = null,
        Func<bool>? runtimeAvailable = null)
    {
        _send = send ?? ((command, payload, token) => new MlWorkerClient().SendAsync(command, payload, token));
        _runtimeAvailable = runtimeAvailable ?? DefaultRuntimeAvailable;
    }

    public async Task<IReadOnlyList<CorrectionRecord>> ApplyAsync(
        ProjectWorkspace workspace,
        UniversalVoiceDatasetRecord dataset,
        CancellationToken cancellationToken = default)
    {
        var targets = dataset.Segments
            .Where(segment =>
                segment.AsrConfidence >= 0.35 && segment.AsrConfidence < 0.65 &&
                segment.AlignmentConfidence >= 0.35 &&
                !segment.SourceId.StartsWith("generated:", StringComparison.Ordinal) &&
                !dataset.Overrides.Any(item =>
                    item.SegmentId == segment.SegmentId &&
                    string.Equals(item.Field, "transcript", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(segment => segment.SegmentId, StringComparer.Ordinal)
            .ToArray();

        if (targets.Length == 0) return [];

        if (!_runtimeAvailable())
            return targets.Select(segment => Deferred(
                segment,
                "文字起こし補正用の同梱ワーカーまたはASRモデルが未準備のため、観測値を保持します。"))
                .ToArray();

        var result = new List<CorrectionRecord>();
        var resourcesRoot = Path.Combine(
            AppContext.BaseDirectory, "workers", "models", "phoneme-supplement");

        foreach (var segment in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _send("review_transcript", new
            {
                audio_path = Path.GetFullPath(segment.AudioPath, workspace.RootPath),
                observed_transcript = segment.Transcript,
                observed_confidence = segment.AsrConfidence,
                resources_root = resourcesRoot
            }, cancellationToken);

            var review = response.Deserialize<TranscriptReview>(Json)
                ?? throw new InvalidDataException("文字起こし補正の再確認結果がありません。");

            Validate(review);

            if (!review.MachinePassed)
            {
                result.Add(Deferred(
                    segment,
                    review.Reasons.Count == 0
                        ? "再認識結果が自動補正条件を満たさないため、観測値を保持します。"
                        : string.Join(" ", review.Reasons),
                    review.Confidence));
                continue;
            }

            var original = segment.Transcript;
            segment.Transcript = review.CandidateTranscript;
            segment.AsrConfidence = review.Confidence;
            segment.AlignmentConfidence = Math.Clamp(review.Confidence * 0.8, 0, 1);
            segment.Phonemes.Clear();

            var step = review.DurationSec / review.CandidatePhonemes.Count;
            for (var index = 0; index < review.CandidatePhonemes.Count; index++)
            {
                segment.Phonemes.Add(new PhonemeTimingRecord
                {
                    Phoneme = review.CandidatePhonemes[index],
                    StartSec = index * step,
                    EndSec = index == review.CandidatePhonemes.Count - 1
                        ? review.DurationSec
                        : (index + 1) * step,
                    Confidence = segment.AlignmentConfidence
                });
            }

            result.Add(new CorrectionRecord
            {
                SegmentId = segment.SegmentId,
                SpeakerId = segment.SpeakerId,
                State = CorrectionState.Corrected,
                Method = ReviewVersion,
                ApplicationStatus = CorrectionApplicationStatus.Applied,
                Confidence = review.Confidence,
                OriginalValue = original,
                CorrectedValue = review.CandidateTranscript,
                Reason = "再認識2方式の音素列が一致し、観測値より信頼度が十分に改善したため、学習用投影だけを補正しました。"
            });
        }

        return result;
    }

    private static CorrectionRecord Deferred(
        UniversalVoiceSegment segment,
        string reason,
        double confidence = 0) => new()
    {
        SegmentId = segment.SegmentId,
        SpeakerId = segment.SpeakerId,
        State = CorrectionState.WeakObserved,
        Method = ReviewVersion,
        ApplicationStatus = CorrectionApplicationStatus.Deferred,
        Confidence = confidence,
        OriginalValue = segment.Transcript,
        Reason = reason
    };

    private static void Validate(TranscriptReview review)
    {
        if (review.ReviewVersion != ReviewVersion ||
            !double.IsFinite(review.Confidence) || review.Confidence is < 0 or > 1 ||
            !double.IsFinite(review.DurationSec) || review.DurationSec <= 0 ||
            review.CandidatePhonemes.Count > 512 ||
            review.Reasons is null)
            throw new InvalidDataException("文字起こし補正の再確認結果が不正です。");

        if (review.MachinePassed &&
            (!review.Changed ||
             string.IsNullOrWhiteSpace(review.CandidateTranscript) ||
             review.CandidatePhonemes.Count == 0 ||
             review.Confidence < 0.72))
            throw new InvalidDataException("文字起こし補正の採用条件と結果が一致しません。");
    }

    private static bool DefaultRuntimeAvailable()
    {
        var executable = OperatingSystem.IsWindows() ? "YourSinger.ML.exe" : "YourSinger.ML";
        return File.Exists(Path.Combine(AppContext.BaseDirectory, "workers", executable)) &&
               Directory.Exists(Path.Combine(
                   AppContext.BaseDirectory, "workers", "models", "phoneme-supplement", "asr"));
    }

    private sealed class TranscriptReview
    {
        public required string ReviewVersion { get; init; }
        public required string CandidateTranscript { get; init; }
        public required List<string> CandidatePhonemes { get; init; }
        public required double Confidence { get; init; }
        public required double DurationSec { get; init; }
        public required bool Changed { get; init; }
        public required bool MachinePassed { get; init; }
        public required List<string> Reasons { get; init; }
    }
}
