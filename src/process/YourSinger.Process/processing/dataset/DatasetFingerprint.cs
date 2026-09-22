using System.Security.Cryptography;
using System.Text.Json;
using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset;

/// <summary>時刻・絶対パス・列挙順・表示言語によらない、学習入力の内容識別。</summary>
public static class DatasetFingerprint
{
    public static string ForView(UniversalVoiceDatasetRecord dataset, bool enabled,
        IReadOnlyList<CorrectionRecord> corrections) => Hash(new
    {
        format = "dataset-fingerprint-v2",
        dataset.SchemaVersion,
        dataset.StageVersion,
        correctionVersion = AutoCorrectionService.StageVersion,
        enabled,
        segments = dataset.Segments.OrderBy(x => x.SegmentId, StringComparer.Ordinal).Select(x => new
        {
            x.SegmentId, x.SourceId, x.SpeakerId, x.ContentType, x.CacheKey,
            x.Transcript, x.AsrConfidence, x.AlignmentConfidence,
            x.Phonemes, x.PitchReliability, x.MeanF0Hz, x.F0StdDevHz,
            x.MeanEnergy, x.VoicedRatio, x.StyleProsody, x.SpeakerEmbedding
        }),
        corrections = corrections.OrderBy(x => x.SegmentId, StringComparer.Ordinal)
            .ThenBy(x => x.Phoneme, StringComparer.Ordinal).ThenBy(x => x.Method, StringComparer.Ordinal)
            .ThenBy(x => x.State).ThenBy(x => x.Confidence)
    });

    public static string ForJob(string viewFingerprint, string speakerId, TrainingTarget target,
        IEnumerable<string> segmentIds) => Hash(new
    {
        format = "training-fingerprint-v2", viewFingerprint, speakerId, target,
        segmentIds = segmentIds.Order(StringComparer.Ordinal).ToArray()
    });

    private static string Hash<T>(T value) => "sha256-v2:" + Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
