using System.Text.Json;
using System.Security.Cryptography;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset;
using YourSinger.Process.Processing.Singing;
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
            SegmentId = id,
            SourceId = source,
            SpeakerId = speaker,
            ContentType = type,
            AudioPath = $"segments/{id}.wav",
            CacheKey = $"sha256-{id}",
            Transcript = "あいうえお",
            AsrConfidence = confidence,
            AlignmentConfidence = confidence,
            AlignmentMethod = DiffSingerDatasetBuilder.RequiredAlignmentMethod,
            PitchReliability = 0.95,
            MeanF0Hz = 220,
            F0FeaturePath = $"features/{id}-f0.json",
            EnergyFeaturePath = $"features/{id}-energy.json",
            VoicingFeaturePath = $"features/{id}-voicing.json",
            Phonemes = [new() { Phoneme = "a", StartSec = 0, EndSec = duration, Confidence = 0.95 }]
        };

    public async Task SeedAsync(params UniversalVoiceSegment[] segments)
    {
        foreach (var segment in segments)
        {
            WriteWave(Path.Combine(Workspace.RootPath, segment.AudioPath), Math.Max(0.1, Duration(segment)));
            if (segment.AlignmentMethod == DiffSingerDatasetBuilder.RequiredAlignmentMethod &&
                segment.Phonemes.Count > 0 && segment.Phonemes.All(x => x.EndSec > x.StartSec && x.StartSec >= 0 && MfaPhoneMap.Encode(x.Phoneme) is not null) &&
                segment.Phonemes.Zip(segment.Phonemes.Skip(1), (a, b) => b.StartSec >= a.EndSec - 1e-6).All(x => x))
                await WriteMeasurementAsync(segment);
            foreach (var path in new[] { segment.F0FeaturePath, segment.EnergyFeaturePath, segment.VoicingFeaturePath })
            {
                // 実ワーカーと同じ時刻付き配列にする。以前の[220,220]は契約を検証できなかった。
                var value = path == segment.F0FeaturePath ? 220.0 : path == segment.EnergyFeaturePath ? 0.03 : 1.0;
                var frames = new[] { new { time_sec = 0.0, value, confidence = 0.95 }, new { time_sec = 0.01, value, confidence = 0.95 } };
                await File.WriteAllTextAsync(Path.Combine(Workspace.RootPath, path), JsonSerializer.Serialize(frames), Token);
            }
        }
        await DatasetRepository.SaveAsync(Workspace, new()
        {
            SchemaVersion = "1",
            StageVersion = "fixture-v1",
            Segments = [.. segments]
        }, Token);
        var speakerRecord = new SpeakerAnalysisRecord { StageVersion = "fixture-v1" };
        foreach (var group in segments.GroupBy(x => x.SpeakerId!))
        {
            var duration = group.Sum(Duration);
            speakerRecord.Speakers.Add(new()
            {
                SpeakerId = group.Key,
                DisplayName = $"話者 {group.Key}",
                TotalDurationSec = duration,
                UsableDurationSec = duration,
                RepresentativeSegmentIds = group.Select(x => x.SegmentId).Take(3).ToList(),
                EmbeddingCentroid = [1, 0]
            });
            speakerRecord.Assignments.AddRange(group.Select(x => new SpeakerSegmentAssignment
            {
                SegmentId = x.SegmentId,
                SpeakerId = group.Key,
                Confidence = 0.95
            }));
        }
        await Speakers.SaveAsync(Workspace, speakerRecord, Token);
        await Classifications.SaveAsync(Workspace, new()
        {
            StageVersion = "fixture-v1",
            Classifications = segments.Select(x => new SegmentContentClassification
            {
                SegmentId = x.SegmentId,
                ContentType = x.ContentType,
                Confidence = 0.95
            }).ToList()
        }, Token);
        foreach (var group in segments.GroupBy(x => x.SourceId))
            await new AudioPreprocessingRepository().SaveAsync(Workspace, new()
            {
                SourceId = group.Key,
                StageVersion = "fixture-v1",
                RawAudioPath = "extracted/source.wav",
                Segments = group.Select(x => new AudioSegmentRecord
                {
                    SegmentId = x.SegmentId,
                    AudioPath = x.AudioPath,
                    StartSec = 0,
                    EndSec = Duration(x)
                }).ToList()
            }, Token);
    }

    // { 責務: [WriteMeasurementAsync: 合成WAVと音素に対応する架空のMFA記録を用意する] }
    private async Task WriteMeasurementAsync(UniversalVoiceSegment segment)
    {
        var audioPath = Path.Combine(Workspace.RootPath, segment.AudioPath);
        var audioHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(audioPath, Token)));
        var duration = Math.Max(0.1, Duration(segment));
        var record = new
        {
            schema_version = 1,
            audio_sha256 = audioHash,
            duration_sec = duration,
            conditions = new
            {
                alignment_method = DiffSingerDatasetBuilder.RequiredAlignmentMethod,
                mfa_version = "3.4.2",
                model_sha256 = "85928ffb1024486872a677a92e1fa94d2f997462d0b84c73a58aa9bb0e35179a"
            },
            phonemes = segment.Phonemes.Select(x => new { phoneme = x.Phoneme, start_sec = x.StartSec, end_sec = x.EndSec, confidence = x.Confidence }),
            mfa_intervals = segment.Phonemes.Select(x => new { label = MfaPhoneMap.Encode(x.Phoneme), start_sec = x.StartSec, end_sec = x.EndSec }),
            confidence_calibrated = false,
            breath_classification_run = false
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        var relative = $"alignment/measurements/{segment.SegmentId}.json";
        var path = Path.Combine(Workspace.RootPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, Token);
        segment.AlignmentMeasurement = new() { Path = relative, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), AudioSha256 = audioHash };
    }

    // { 責務: [RefreshMeasurementAsync: 合成音声を差し替えたテスト素材の測定記録を作り直す] }
    public async Task RefreshMeasurementAsync(UniversalVoiceSegment segment)
    {
        await WriteMeasurementAsync(segment);
        var dataset = await DatasetRepository.LoadAsync(Workspace, Token) ?? throw new InvalidDataException();
        dataset.Segments.Single(x => x.SegmentId == segment.SegmentId).AlignmentMeasurement = segment.AlignmentMeasurement;
        await DatasetRepository.SaveAsync(Workspace, dataset, Token);
    }

    public static double Duration(UniversalVoiceSegment segment) => segment.Phonemes.Max(x => x.EndSec);

    public static void WriteWave(string path, double durationSec = 0.1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int sampleRate = 48000;
        var samples = (int)Math.Round(sampleRate * durationSec);
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
