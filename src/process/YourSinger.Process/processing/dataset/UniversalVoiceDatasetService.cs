using System.Text.Json;
using System.Text.Json.Serialization;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Dataset;

public sealed class UniversalVoiceDatasetService
{
    public const string SchemaVersion = "1";
    public const string StageVersion = "v1-05.1";

    private readonly MlWorkerClient _worker;
    private readonly AudioPreprocessingRepository _preprocessingRepository;
    private readonly SpeakerAnalysisRepository _speakerRepository;
    private readonly ContentClassificationRepository _classificationRepository;
    private readonly UniversalVoiceDatasetRepository _datasetRepository;
    private readonly FeatureCacheKeyService _cacheKeyService;

    public UniversalVoiceDatasetService(
        MlWorkerClient worker,
        AudioPreprocessingRepository preprocessingRepository,
        SpeakerAnalysisRepository speakerRepository,
        ContentClassificationRepository classificationRepository,
        UniversalVoiceDatasetRepository datasetRepository,
        FeatureCacheKeyService cacheKeyService)
    {
        _worker = worker;
        _preprocessingRepository = preprocessingRepository;
        _speakerRepository = speakerRepository;
        _classificationRepository = classificationRepository;
        _datasetRepository = datasetRepository;
        _cacheKeyService = cacheKeyService;
    }

    public async Task<UniversalVoiceDatasetRecord> BuildAsync(
        ProjectWorkspace workspace,
        IEnumerable<SourceRecord> sources,
        CancellationToken cancellationToken = default)
    {
        var existing = await _datasetRepository.LoadAsync(workspace, cancellationToken);
        var existingBySegment = existing?.Segments.ToDictionary(x => x.SegmentId)
            ?? new Dictionary<string, UniversalVoiceSegment>(StringComparer.Ordinal);

        var speakers = await _speakerRepository.LoadAsync(workspace, cancellationToken);
        var speakerBySegment = speakers?.Assignments.ToDictionary(
            x => x.SegmentId,
            x => x.SpeakerId)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var speakerEmbeddingById = speakers?.Speakers.ToDictionary(
            x => x.SpeakerId,
            x => x.EmbeddingCentroid)
            ?? new Dictionary<string, List<double>>(StringComparer.Ordinal);

        var classification = await _classificationRepository.LoadAsync(
            workspace,
            cancellationToken);

        var contentBySegment = classification?.Classifications.ToDictionary(
            x => x.SegmentId)
            ?? new Dictionary<string, SegmentContentClassification>(StringComparer.Ordinal);

        var dataset = new UniversalVoiceDatasetRecord
        {
            SchemaVersion = SchemaVersion,
            StageVersion = StageVersion
        };

        if (existing is not null)
        {
            dataset.Overrides.AddRange(existing.Overrides);
        }

        foreach (var source in sources)
        {
            var preprocessing = await _preprocessingRepository.LoadAsync(
                workspace,
                source.SourceId,
                cancellationToken);

            if (preprocessing is null || preprocessing.ErrorMessage is not null)
            {
                continue;
            }

            foreach (var segment in preprocessing.Segments)
            {
                var audioPath = Path.Combine(workspace.RootPath, segment.AudioPath);
                var cacheKey = await _cacheKeyService.CreateAsync(
                    audioPath,
                    StageVersion,
                    cancellationToken);

                if (existingBySegment.TryGetValue(segment.SegmentId, out var cached) &&
                    cached.CacheKey == cacheKey &&
                    FeatureFilesExist(workspace, cached))
                {
                    dataset.Segments.Add(cached);
                    if (existing is not null)
                    {
                        dataset.Provenance.AddRange(
                            existing.Provenance.Where(x =>
                                x.SegmentId == cached.SegmentId &&
                                x.CacheKey == cached.CacheKey));
                    }
                    continue;
                }

                var result = await _worker.SendAsync(
                    "extract_universal_features",
                    new
                    {
                        segment_id = segment.SegmentId,
                        source_id = source.SourceId,
                        audio_path = audioPath,
                        workspace_path = workspace.RootPath,
                        cache_key = cacheKey,
                        stage_version = StageVersion
                    },
                    cancellationToken);

                var feature = ParseSegment(result);
                feature.ContentType = contentBySegment.TryGetValue(
                    segment.SegmentId,
                    out var classified)
                    ? classified.ContentType
                    : SegmentContentType.Ambiguous;

                if (speakerBySegment.TryGetValue(segment.SegmentId, out var speakerId))
                {
                    feature.SpeakerId = speakerId;
                    if (speakerEmbeddingById.TryGetValue(speakerId, out var embedding))
                    {
                        feature.SpeakerEmbedding.AddRange(embedding);
                    }
                }

                dataset.Segments.Add(feature);
                AddProvenance(dataset, feature);
            }
        }

        ApplyOverrides(dataset);
        RebuildCoverage(dataset);

        await _datasetRepository.SaveAsync(workspace, dataset, cancellationToken);
        return dataset;
    }

    private static UniversalVoiceSegment ParseSegment(JsonElement result)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        return JsonSerializer.Deserialize<UniversalVoiceSegment>(
            result.GetRawText(),
            options) ?? throw new InvalidDataException(
                "共通特徴抽出結果を読み取れませんでした。");
    }

    private static bool FeatureFilesExist(
        ProjectWorkspace workspace,
        UniversalVoiceSegment segment) =>
        Exists(workspace, segment.F0FeaturePath) &&
        Exists(workspace, segment.EnergyFeaturePath) &&
        Exists(workspace, segment.VoicingFeaturePath);

    private static bool Exists(ProjectWorkspace workspace, string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(workspace.RootPath, path));

    private static void AddProvenance(
        UniversalVoiceDatasetRecord dataset,
        UniversalVoiceSegment segment)
    {
        foreach (var (feature, method) in new[]
        {
            ("transcript", "faster-whisper"),
            ("phonemes", "pyopenjtalk"),
            ("f0", "autocorrelation"),
            ("energy", "rms"),
            ("voicing", "pitch-confidence"),
            ("style_prosody", "summary-statistics"),
            ("speaker_embedding", "speaker-centroid")
        })
        {
            dataset.Provenance.Add(new FeatureProvenanceRecord
            {
                SegmentId = segment.SegmentId,
                FeatureName = feature,
                StageVersion = StageVersion,
                Method = method,
                CacheKey = segment.CacheKey
            });
        }
    }

    private static void ApplyOverrides(UniversalVoiceDatasetRecord dataset)
    {
        var byId = dataset.Segments.ToDictionary(x => x.SegmentId);
        foreach (var userOverride in dataset.Overrides.OrderBy(x => x.ChangedAt))
        {
            if (!byId.TryGetValue(userOverride.SegmentId, out var segment))
            {
                continue;
            }

            if (userOverride.Field == "transcript")
            {
                segment.Transcript = userOverride.Value;
            }
        }
    }

    private static void RebuildCoverage(UniversalVoiceDatasetRecord dataset)
    {
        dataset.Coverage.PhonemeCounts.Clear();
        dataset.Coverage.ContentTypeCounts.Clear();

        foreach (var segment in dataset.Segments)
        {
            var contentKey = segment.ContentType.ToString().ToLowerInvariant();
            dataset.Coverage.ContentTypeCounts[contentKey] =
                dataset.Coverage.ContentTypeCounts.GetValueOrDefault(contentKey) + 1;

            foreach (var phoneme in segment.Phonemes)
            {
                dataset.Coverage.PhonemeCounts[phoneme.Phoneme] =
                    dataset.Coverage.PhonemeCounts.GetValueOrDefault(phoneme.Phoneme) + 1;
            }
        }
    }
}
