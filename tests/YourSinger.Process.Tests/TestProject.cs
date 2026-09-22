using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Training;

namespace YourSinger.Process.Tests;

// 実音声モデルの代わりに、境界テスト用の明示的な解析結果を用意する。
// このデータで音声認識・話者識別・学習品質の合格を主張しない。
internal sealed class TestProject : IDisposable
{
    public ProjectWorkspace Workspace { get; } = new(Path.Combine(
        Path.GetTempPath(), "YourSinger.Tests", Guid.NewGuid().ToString("N")));
    public UniversalVoiceDatasetRepository DatasetRepository { get; } = new();
    public SpeakerAnalysisRepository Speakers { get; } = new();
    public ContentClassificationRepository Classifications { get; } = new();
    public TrainingJobRepository Jobs { get; } = new();
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public TestProject() => Workspace.EnsureCreated();

    public AutoCorrectionService Correction() => new(DatasetRepository, new AutoCorrectionRepository());
    public TrainingJobPlanningService Planner() => new(DatasetRepository, Correction(), Jobs);

    public UniversalVoiceSegment Segment(string id, string speaker = "spk_a",
        SegmentContentType type = SegmentContentType.Speech, double duration = 2,
        double confidence = 0.95, string source = "src_a") => new()
    {
        SegmentId = id, SourceId = source, SpeakerId = speaker, ContentType = type,
        AudioPath = $"segments/{id}.wav", CacheKey = $"sha256-{id}",
        Transcript = "あいうえお", AsrConfidence = confidence, AlignmentConfidence = confidence,
        PitchReliability = 0.95, MeanF0Hz = 220,
        F0FeaturePath = $"features/{id}-f0.json", EnergyFeaturePath = $"features/{id}-energy.json",
        VoicingFeaturePath = $"features/{id}-voicing.json",
        Phonemes = [new() { Phoneme = "a", StartSec = 0, EndSec = duration, Confidence = 0.95 }]
    };

    public async Task SeedAsync(params UniversalVoiceSegment[] segments)
    {
        foreach (var segment in segments)
        {
            WriteWave(Path.Combine(Workspace.RootPath, segment.AudioPath));
            foreach (var path in new[] { segment.F0FeaturePath, segment.EnergyFeaturePath, segment.VoicingFeaturePath })
                await File.WriteAllTextAsync(Path.Combine(Workspace.RootPath, path), "[220,220]", Token);
        }
        await DatasetRepository.SaveAsync(Workspace, new()
        {
            SchemaVersion = "1", StageVersion = "fixture-v1", Segments = [.. segments]
        }, Token);
        var speakerRecord = new SpeakerAnalysisRecord { StageVersion = "fixture-v1" };
        foreach (var group in segments.GroupBy(x => x.SpeakerId!))
        {
            var duration = group.Sum(Duration);
            speakerRecord.Speakers.Add(new()
            {
                SpeakerId = group.Key, DisplayName = $"話者 {group.Key}",
                TotalDurationSec = duration, UsableDurationSec = duration,
                RepresentativeSegmentIds = group.Select(x => x.SegmentId).Take(3).ToList(),
                EmbeddingCentroid = [1, 0]
            });
            speakerRecord.Assignments.AddRange(group.Select(x => new SpeakerSegmentAssignment
            {
                SegmentId = x.SegmentId, SpeakerId = group.Key, Confidence = 0.95
            }));
        }
        await Speakers.SaveAsync(Workspace, speakerRecord, Token);
        await Classifications.SaveAsync(Workspace, new()
        {
            StageVersion = "fixture-v1",
            Classifications = segments.Select(x => new SegmentContentClassification
            {
                SegmentId = x.SegmentId, ContentType = x.ContentType, Confidence = 0.95
            }).ToList()
        }, Token);
        foreach (var group in segments.GroupBy(x => x.SourceId))
            await new AudioPreprocessingRepository().SaveAsync(Workspace, new()
            {
                SourceId = group.Key, StageVersion = "fixture-v1", RawAudioPath = "extracted/source.wav",
                Segments = group.Select(x => new AudioSegmentRecord
                {
                    SegmentId = x.SegmentId, AudioPath = x.AudioPath, StartSec = 0, EndSec = Duration(x)
                }).ToList()
            }, Token);
    }

    public static double Duration(UniversalVoiceSegment segment) => segment.Phonemes.Max(x => x.EndSec);

    public static void WriteWave(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int sampleRate = 48000;
        const int samples = 4800;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            writer.Write((short)(1000 * Math.Sin(2 * Math.PI * 220 * i / sampleRate)));
    }

    public void Dispose() => Directory.Delete(Workspace.RootPath, recursive: true);
}
