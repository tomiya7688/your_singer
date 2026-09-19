using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;

namespace YourSinger.Process.Processing.Dataset;

public sealed class PhonemeCoverageDiagnosticService
{
    public const string StageVersion = "v1-06.1";
    public const int LowCountThreshold = 3;

    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    private readonly PhonemeCoverageRepository _coverageRepository;

    public PhonemeCoverageDiagnosticService(
        UniversalVoiceDatasetRepository datasetRepository,
        PhonemeCoverageRepository coverageRepository)
    {
        _datasetRepository = datasetRepository;
        _coverageRepository = coverageRepository;
    }

    public async Task<PhonemeCoverageDiagnostic> DiagnoseAsync(
        ProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        var dataset = await _datasetRepository.LoadAsync(workspace, cancellationToken)
            ?? throw new InvalidOperationException("Universal Voice Datasetがありません。");

        var diagnostic = new PhonemeCoverageDiagnostic
        {
            StageVersion = StageVersion
        };

        diagnostic.Scopes.Add(BuildScope(dataset.Segments, null, null));

        foreach (var speakerId in dataset.Segments
                     .Select(x => x.SpeakerId)
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal))
        {
            var speakerSegments = dataset.Segments
                .Where(x => x.SpeakerId == speakerId)
                .ToArray();

            diagnostic.Scopes.Add(BuildScope(
                speakerSegments,
                speakerId,
                null));

            foreach (var contentType in new[]
                     {
                         SegmentContentType.Speech,
                         SegmentContentType.Singing
                     })
            {
                diagnostic.Scopes.Add(BuildScope(
                    speakerSegments.Where(x => x.ContentType == contentType),
                    speakerId,
                    contentType));
            }
        }

        await _coverageRepository.SaveAsync(
            workspace,
            diagnostic,
            cancellationToken);

        return diagnostic;
    }

    public IReadOnlyList<KanaCoverageItem> BuildKanaView(
        CoverageScopeDiagnostic scope)
    {
        var result = new List<KanaCoverageItem>();

        foreach (var (kana, phonemes) in JapaneseCoverageCatalog.KanaToPhonemes)
        {
            var count = phonemes
                .Select(p => scope.PhonemeCounts.GetValueOrDefault(p))
                .DefaultIfEmpty(0)
                .Min();

            result.Add(new KanaCoverageItem
            {
                Kana = kana,
                PhonemeKey = string.Join(" ", phonemes),
                Count = count,
                Quality = QualityForCount(count)
            });
        }

        return result;
    }

    private static CoverageScopeDiagnostic BuildScope(
        IEnumerable<UniversalVoiceSegment> segments,
        string? speakerId,
        SegmentContentType? contentType)
    {
        var scope = new CoverageScopeDiagnostic
        {
            SpeakerId = speakerId,
            ContentType = contentType
        };

        foreach (var segment in segments)
        {
            var phonemes = segment.Phonemes.Select(x => x.Phoneme).ToArray();

            foreach (var phoneme in phonemes)
            {
                scope.PhonemeCounts[phoneme] =
                    scope.PhonemeCounts.GetValueOrDefault(phoneme) + 1;
            }

            for (var index = 0; index < phonemes.Length; index++)
            {
                var previous = index > 0 ? phonemes[index - 1] : "<BOS>";
                var current = phonemes[index];
                var next = index + 1 < phonemes.Length ? phonemes[index + 1] : "<EOS>";
                var key = $"{previous}|{current}|{next}";
                scope.ContextCounts[key] =
                    scope.ContextCounts.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var expected in JapaneseCoverageCatalog.CorePhonemes)
        {
            var count = scope.PhonemeCounts.GetValueOrDefault(expected);
            if (count == 0)
            {
                scope.MissingPhonemes.Add(expected);
            }
            else if (count < LowCountThreshold)
            {
                scope.LowCountPhonemes.Add(expected);
            }
        }

        scope.Categories.Add(BuildCategory(
            "拗音",
            JapaneseCoverageCatalog.Palatalized,
            scope.PhonemeCounts));
        scope.Categories.Add(BuildCategory(
            "濁音",
            JapaneseCoverageCatalog.Voiced,
            scope.PhonemeCounts));
        scope.Categories.Add(BuildCategory(
            "半濁音",
            JapaneseCoverageCatalog.SemiVoiced,
            scope.PhonemeCounts));
        scope.Categories.Add(BuildCategory(
            "促音・撥音・長音",
            JapaneseCoverageCatalog.Special,
            scope.PhonemeCounts));
        scope.Categories.Add(BuildCategory(
            "外来語系拡張音",
            JapaneseCoverageCatalog.Foreign,
            scope.PhonemeCounts));

        foreach (var (kana, mappedPhonemes) in JapaneseCoverageCatalog.KanaToPhonemes)
        {
            var count = mappedPhonemes
                .Select(p => scope.PhonemeCounts.GetValueOrDefault(p))
                .DefaultIfEmpty(0)
                .Min();

            scope.MoraCounts[kana] = count;
        }

        scope.Quality = DetermineOverallQuality(scope);
        return scope;
    }

    private static CoverageCategoryDiagnostic BuildCategory(
        string category,
        IEnumerable<string> expected,
        IReadOnlyDictionary<string, int> counts)
    {
        var expectedArray = expected.Distinct(StringComparer.Ordinal).ToArray();
        var observed = expectedArray.Count(x => counts.GetValueOrDefault(x) > 0);
        var ratio = expectedArray.Length == 0
            ? 1.0
            : (double)observed / expectedArray.Length;

        return new CoverageCategoryDiagnostic
        {
            Category = category,
            Observed = observed,
            Expected = expectedArray.Length,
            Ratio = ratio,
            Quality = ratio switch
            {
                >= 0.80 => CoverageQuality.Good,
                >= 0.50 => CoverageQuality.Warning,
                _ => CoverageQuality.Missing
            }
        };
    }

    private static CoverageQuality DetermineOverallQuality(
        CoverageScopeDiagnostic scope)
    {
        if (scope.MissingPhonemes.Count >= 5)
        {
            return CoverageQuality.Missing;
        }

        if (scope.MissingPhonemes.Count > 0 ||
            scope.LowCountPhonemes.Count > 0 ||
            scope.Categories.Any(x => x.Quality != CoverageQuality.Good))
        {
            return CoverageQuality.Warning;
        }

        return CoverageQuality.Good;
    }

    private static CoverageQuality QualityForCount(int count) =>
        count switch
        {
            0 => CoverageQuality.Missing,
            < LowCountThreshold => CoverageQuality.Warning,
            _ => CoverageQuality.Good
        };
}
