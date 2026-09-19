using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Dataset;

public sealed class SingingQualityDiagnosticService
{
    public const string StageVersion = "v1-07.1";

    private static readonly HashSet<string> Vowels =
        new(StringComparer.Ordinal) { "a", "i", "u", "e", "o" };

    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    private readonly SingingQualityRepository _repository;

    public SingingQualityDiagnosticService(
        UniversalVoiceDatasetRepository datasetRepository,
        SingingQualityRepository repository)
    {
        _datasetRepository = datasetRepository;
        _repository = repository;
    }

    public async Task<SingingQualityDiagnostic> DiagnoseAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        var dataset = await _datasetRepository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("Universal Voice Datasetがありません。");

        var singing = dataset.Segments
            .Where(x => x.ContentType == SegmentContentType.Singing)
            .ToArray();

        var diagnostic = new SingingQualityDiagnostic
        {
            StageVersion = StageVersion
        };

        diagnostic.Scopes.Add(BuildScope(singing, null));

        foreach (var speakerId in singing
                     .Select(x => x.SpeakerId)
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal))
        {
            diagnostic.Scopes.Add(BuildScope(
                singing.Where(x => x.SpeakerId == speakerId),
                speakerId));
        }

        dataset.Coverage.SingingQuality.ScopeCount = diagnostic.Scopes.Count;
        dataset.Coverage.SingingQuality.OverallQuality =
            diagnostic.Scopes.Any(x => x.Quality == SingingQualityLevel.Insufficient)
                ? SingingQualityLevel.Insufficient
                : diagnostic.Scopes.Any(x => x.Quality == SingingQualityLevel.Warning)
                    ? SingingQualityLevel.Warning
                    : SingingQualityLevel.Good;

        await _datasetRepository.SaveAsync(workspace, dataset, cancellationToken);
        await _repository.SaveAsync(workspace, diagnostic, cancellationToken);

        return diagnostic;
    }

    private static SingingQualityScope BuildScope(
        IEnumerable<UniversalVoiceSegment> source,
        string? speakerId)
    {
        var segments = source.ToArray();
        var warnings = new List<string>();

        var usableF0 = segments.Where(x => x.MeanF0Hz > 0).Select(x => x.MeanF0Hz).ToArray();
        var minF0 = usableF0.DefaultIfEmpty(0).Min();
        var maxF0 = usableF0.DefaultIfEmpty(0).Max();

        var lowDuration = 0.0;
        var midDuration = 0.0;
        var highDuration = 0.0;
        var sustainedCount = 0;
        var sustainedDuration = 0.0;

        var vowelCoverage = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal)
        {
            ["low"] = NewVowelMap(),
            ["mid"] = NewVowelMap(),
            ["high"] = NewVowelMap()
        };

        var transitions = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var segment in segments)
        {
            var duration = SegmentDuration(segment);
            var range = PitchRange(segment.MeanF0Hz);

            switch (range)
            {
                case "low": lowDuration += duration; break;
                case "mid": midDuration += duration; break;
                case "high": highDuration += duration; break;
            }

            foreach (var phoneme in segment.Phonemes)
            {
                if (Vowels.Contains(phoneme.Phoneme))
                {
                    vowelCoverage[range][phoneme.Phoneme]++;
                    var phonemeDuration = Math.Max(0, phoneme.EndSec - phoneme.StartSec);
                    if (phonemeDuration >= 0.30)
                    {
                        sustainedCount++;
                        sustainedDuration += phonemeDuration;
                    }
                }
            }

            for (var i = 0; i + 1 < segment.Phonemes.Count; i++)
            {
                var current = segment.Phonemes[i].Phoneme;
                var next = segment.Phonemes[i + 1].Phoneme;
                if (!Vowels.Contains(current) && Vowels.Contains(next))
                {
                    var key = $"{current}->{next}";
                    transitions[key] = transitions.GetValueOrDefault(key) + 1;
                }
            }
        }

        var totalDuration = segments.Sum(SegmentDuration);
        var reliability = segments.Length == 0
            ? 0
            : segments.Average(x => x.PitchReliability);

        var longToneQuality = sustainedCount == 0
            ? 0
            : Math.Clamp(reliability * Math.Min(1.0, sustainedDuration / 8.0), 0, 1);

        if (usableF0.Length == 0)
            warnings.Add("有効なF0が不足しています。");
        else if (maxF0 / Math.Max(minF0, 1) < 1.5)
            warnings.Add("音域の広がりが小さい可能性があります。");

        if (lowDuration < 5)
            warnings.Add("低音域の歌唱データが少量です。");
        if (highDuration < 5)
            warnings.Add("高音域の歌唱データが少量です。");
        if (sustainedCount < 5)
            warnings.Add("母音のロングトーンが少量です。");
        if (reliability < 0.60)
            warnings.Add("F0 / pitch reliabilityが低めです。");

        foreach (var range in new[] { "low", "mid", "high" })
        {
            var missing = Vowels.Where(v => vowelCoverage[range][v] == 0).ToArray();
            if (missing.Length > 0)
                warnings.Add($"{RangeLabel(range)}で不足している母音: {string.Join(", ", missing)}");
        }

        var quality = totalDuration < 10 || usableF0.Length == 0
            ? SingingQualityLevel.Insufficient
            : warnings.Count > 0
                ? SingingQualityLevel.Warning
                : SingingQualityLevel.Good;

        return new SingingQualityScope
        {
            SpeakerId = speakerId,
            SegmentCount = segments.Length,
            TotalDurationSec = totalDuration,
            MinF0Hz = minF0,
            MaxF0Hz = maxF0,
            MeanPitchReliability = reliability,
            LowRangeDurationSec = lowDuration,
            MidRangeDurationSec = midDuration,
            HighRangeDurationSec = highDuration,
            VowelCoverageByRange = vowelCoverage,
            ConsonantVowelTransitions = transitions,
            SustainedVowelCount = sustainedCount,
            SustainedVowelDurationSec = sustainedDuration,
            LongToneQuality = longToneQuality,
            Warnings = warnings,
            Quality = quality
        };
    }

    private static Dictionary<string, int> NewVowelMap() =>
        Vowels.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);

    private static double SegmentDuration(UniversalVoiceSegment segment) =>
        segment.Phonemes.Count == 0 ? 0 : segment.Phonemes.Max(x => x.EndSec);

    private static string PitchRange(double f0) =>
        f0 <= 0 ? "mid" :
        f0 < 165 ? "low" :
        f0 >= 330 ? "high" :
        "mid";

    private static string RangeLabel(string range) => range switch
    {
        "low" => "低音域",
        "high" => "高音域",
        _ => "中音域"
    };
}
