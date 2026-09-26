namespace YourSinger.Data.Models;

public sealed class PhonemeSupplementCandidate
{
    public required string CandidateId { get; init; }
    public required string SpeakerId { get; init; }
    public required string Text { get; init; }
    public required string ModelSpeaker { get; init; }
    public required string ModelDirectory { get; init; }
    public required string ReferenceSegmentId { get; init; }
    public required string ObservationFingerprint { get; init; }
    public required string AudioPath { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required PhonemeSupplementVerification Verification { get; init; }
}

// 機械検証の通過と、利用者による学習への採用は別に保存する。
public sealed class PhonemeSupplementVerification
{
    public required string GeneratorVersion { get; init; }
    public required string ModelFingerprint { get; init; }
    public required string ReferenceSha256 { get; init; }
    public required string AudioSha256 { get; init; }
    public required string RecognizedText { get; init; }
    public required List<string> ExpectedPhonemes { get; init; }
    public required List<string> RecognizedPhonemes { get; init; }
    public required List<string> MissingPhonemes { get; init; }
    public required double SpeakerSimilarity { get; init; }
    public required double AsrAvgLogprob { get; init; }
    public required double NoSpeechProbability { get; init; }
    public required double DurationSec { get; init; }
    public required double Rms { get; init; }
    public required double ClippingRatio { get; init; }
    public required double SilenceRatio { get; init; }
    public required bool MachinePassed { get; init; }
    public required List<string> Reasons { get; init; }
}

public sealed class PhonemeSupplementSelection
{
    // 初期実装では話者ごとに一つの文章だけを採用できる。
    public Dictionary<string, string> CandidateBySpeaker { get; init; } = new(StringComparer.Ordinal);
}


public sealed class PhonemeSupplementRuntimeStatus
{
    public bool Ready { get; init; }
    public bool FullVerify { get; init; }
    public Dictionary<string, string> Versions { get; init; } = new(StringComparer.Ordinal);
    public List<PhonemeSupplementRuntimeResourceStatus> Resources { get; init; } = [];
    public List<string> Issues { get; init; } = [];
}

public sealed class PhonemeSupplementRuntimeResourceStatus
{
    public required string Name { get; init; }
    public int CheckedFiles { get; init; }
}
