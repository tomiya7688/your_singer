using System.Text.Json;
using System.Text.Json.Serialization;

namespace YourSinger.Data.Models;

public sealed record PitchCompletionSettings
{
    public bool Enabled { get; init; } = true;
    public double MaxGapSec { get; init; } = 0.06;
    public double MinAnchorConfidence { get; init; } = 0.80;
    public double MaxBoundarySemitones { get; init; } = 2.0;
    public double MinPeriodicity { get; init; } = 0.90;
    public double MinRms { get; init; } = 0.008;
    public double MinPhonemeConfidence { get; init; } = 0.65;
}

// universal_features.pyの時刻付きJSON配列と同じ契約。エネルギー・有声性にはconfidenceがない場合がある。
public sealed record AudioFeatureFrame
{
    public required double TimeSec { get; init; }
    public required double Value { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Confidence { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
