using System.Globalization;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Dataset.Completion;
using YourSinger.Process.Processing.Singing;
using YourSinger.Process.Processing.Speaker;

namespace YourSinger.Process.Tests;

public sealed class PitchCompletionTests
{
    private static CancellationToken Token => TestProject.Token;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static AudioFeatureFrame[] Frames(Func<int, double> value, bool confidence = false) =>
        Enumerable.Range(0, 20).Select(i => new AudioFeatureFrame
        {
            TimeSec = i * 0.01, Value = value(i), Confidence = confidence ? value(i) == 0 ? 0 : 0.95 : null
        }).ToArray();

    private static List<PhonemeTimingRecord> Vowel(string name = "a") =>
        [new() { Phoneme = name, StartSec = 0, EndSec = 0.30, Confidence = 0.95 }];

    [Fact]
    public void GapValuesUseLogFrequencyAndDoNotChangeAnchors()
    {
        var frames = Frames(i => i is 4 or 5 ? 0 : i < 6 ? 220 : 246, true);
        var original = frames.ToArray();
        var result = new PitchGapInterpolator().Complete(frames, Frames(_ => 0.05), Frames(i => i is 4 or 5 ? 0 : 1),
            Vowel(), new(), (_, _, _) => new(0.99, 0.05), Token);
        var change = Assert.Single(result.Decisions);
        Assert.True(change.Applied);
        Assert.Equal(4, change.StartFrame); Assert.Equal(6, change.EndFrameExclusive);
        Assert.Equal(220 * Math.Pow(246.0 / 220, 1.0 / 3), result.F0[4].Value, 8);
        Assert.Equal(220 * Math.Pow(246.0 / 220, 2.0 / 3), result.F0[5].Value, 8);
        Assert.Equal(1, result.Voicing[4].Value);
        Assert.Equal(original, frames);
        Assert.Equal(frames[3], result.F0[3]); Assert.Equal(frames[6], result.F0[6]);
        Assert.InRange(change.Confidence, 0.01, 0.65);
    }

    [Theory]
    [InlineData("先頭")]
    [InlineData("末尾")]
    [InlineData("長い欠損")]
    [InlineData("無音")]
    [InlineData("周期性不足")]
    [InlineData("実波形の無音")]
    [InlineData("子音")]
    [InlineData("音素境界")]
    [InlineData("低信頼音素")]
    [InlineData("無声の端点")]
    [InlineData("低信頼端点")]
    [InlineData("音高の段差")]
    [InlineData("不安定な端点")]
    public void UnsupportedGapsRemainUnchanged(string scenario)
    {
        var f0 = Frames(i => i is 4 or 5 ? 0 : 220, true);
        var energy = Frames(_ => 0.05);
        var voicing = Frames(i => i is 4 or 5 ? 0 : 1);
        var phonemes = Vowel();
        var evidence = new PitchEvidence(0.99, 0.05);
        switch (scenario)
        {
            case "先頭": f0 = Frames(i => i < 2 ? 0 : 220, true); break;
            case "末尾": f0 = Frames(i => i > 17 ? 0 : 220, true); break;
            case "長い欠損": f0 = Frames(i => i >= 4 && i < 13 ? 0 : 220, true); break;
            case "無音": energy[4] = energy[4] with { Value = 0 }; break;
            case "周期性不足": evidence = new(0.2, 0.05); break;
            case "実波形の無音": evidence = new(0.99, 0); break;
            case "子音": phonemes = Vowel("s"); break;
            case "音素境界": phonemes = [new() { Phoneme = "a", StartSec = 0, EndSec = 0.05, Confidence = 0.95 }, new() { Phoneme = "i", StartSec = 0.05, EndSec = 0.30, Confidence = 0.95 }]; break;
            case "低信頼音素": phonemes = [new() { Phoneme = "a", StartSec = 0, EndSec = 0.30, Confidence = 0.3 }]; break;
            case "無声の端点": voicing[3] = voicing[3] with { Value = 0 }; break;
            case "低信頼端点": f0[3] = f0[3] with { Confidence = 0.6 }; break;
            case "音高の段差": f0 = Frames(i => i is 4 or 5 ? 0 : i < 6 ? 220 : 440, true); break;
            case "不安定な端点": f0[2] = f0[2] with { Value = 200 }; break;
        }
        var result = new PitchGapInterpolator().Complete(f0, energy, voicing, phonemes, new(), (_, _, _) => evidence, Token);
        Assert.Equal(f0, result.F0);
        Assert.Equal(voicing, result.Voicing);
        Assert.NotEmpty(result.Decisions);
        Assert.All(result.Decisions, x => { Assert.False(x.Applied); Assert.Empty(x.ResultValues); });
    }

    [Fact]
    public void StableObservedPitchIsNeverReplaced()
    {
        var f0 = Frames(i => i is 4 or 5 ? 440 : 220, true);
        var result = new PitchGapInterpolator().Complete(f0, Frames(_ => 0.05), Frames(_ => 1), Vowel(), new(),
            (_, _, _) => throw new InvalidOperationException("観測値を再推定してはいけません。"), Token);
        Assert.Equal(f0, result.F0); Assert.Empty(result.Decisions);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void InvalidLimitsAreRejected(double maxGap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PitchGapInterpolator.ValidateSettings(new() { MaxGapSec = maxGap }));
    }

    [Theory]
    [InlineData("時刻")]
    [InlineData("非有限値")]
    [InlineData("要素数")]
    [InlineData("負の値")]
    public void MalformedFeaturesAreRejected(string scenario)
    {
        var f0 = Frames(_ => 220, true);
        var energy = Frames(_ => 0.05);
        if (scenario == "時刻") energy[2] = energy[2] with { TimeSec = 0.123 };
        if (scenario == "非有限値") f0[4] = f0[4] with { Value = double.NaN };
        if (scenario == "要素数") energy = energy[..^1];
        if (scenario == "負の値") f0[4] = f0[4] with { Value = -1 };
        Assert.Throws<InvalidDataException>(() => new PitchGapInterpolator().Complete(f0, energy, Frames(_ => 1), Vowel(), new(), (_, _, _) => new(1, 1), Token));
    }

    [Fact]
    public async Task ActualCompletionWritesSeparateFeaturesAndPreservesAllOriginals()
    {
        using var p = new TestProject();
        var segment = await SeedPitchAsync(p);
        var paths = new[] { segment.AudioPath, segment.F0FeaturePath, segment.EnergyFeaturePath, segment.VoicingFeaturePath, "metadata/universal-voice-dataset.json" };
        var original = new Dictionary<string, byte[]>();
        foreach (var path in paths) original[path] = await File.ReadAllBytesAsync(Path.Combine(p.Workspace.RootPath, path), Token);
        var view = await p.Correction().BuildAsync(p.Workspace, true, Token);
        var completed = Assert.Single(view.Segments);
        var applied = Assert.Single(view.AppliedCorrections, x => x.Method == PitchGapInterpolator.Version && x.ApplicationStatus == CorrectionApplicationStatus.Applied);
        Assert.Equal(CorrectionState.Estimated, applied.State);
        Assert.Equal(new double[] { 0, 0 }, applied.OriginalValues);
        Assert.All(applied.ResultValues, x => Assert.Equal(220, x, 8));
        Assert.Equal(completed.F0FeaturePath, applied.ResultFeaturePath);
        var f0 = await ReadFramesAsync(p, completed.F0FeaturePath);
        var voiced = await ReadFramesAsync(p, completed.VoicingFeaturePath);
        Assert.Equal(220, f0[4].Value, 8); Assert.Equal(1, voiced[4].Value);
        Assert.Equal(segment.PitchReliability, completed.PitchReliability);
        Assert.All(view.AppliedCorrections.Where(x => x.Method == "missing-phoneme-generation"), x =>
        {
            Assert.Equal(CorrectionApplicationStatus.Deferred, x.ApplicationStatus);
            Assert.Empty(x.ResultValues); Assert.Null(x.CorrectedValue);
        });
        foreach (var path in paths) Assert.Equal(original[path], await File.ReadAllBytesAsync(Path.Combine(p.Workspace.RootPath, path), Token));
    }

    [Fact]
    public async Task OffDoesNotCreateFeaturesOrChangeOriginalPaths()
    {
        using var p = new TestProject();
        var source = await SeedPitchAsync(p);
        var view = await p.Correction().BuildAsync(p.Workspace, false, Token);
        Assert.Equal(source.F0FeaturePath, Assert.Single(view.Segments).F0FeaturePath);
        Assert.All(view.AppliedCorrections, x => Assert.Equal(CorrectionApplicationStatus.NotRequired, x.ApplicationStatus));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.FeaturesPath, "completion")));
    }

    [Fact]
    public async Task CompletedValuesReachDiffSingerInputAndOffUsesOriginals()
    {
        using var p = new TestProject();
        var source = await SeedPitchAsync(p);
        foreach (var enabled in new[] { true, false })
        {
            var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace,
                [new() { SpeakerId = "spk_a", Target = TrainingTarget.Singing, AutoCorrectionEnabled = enabled }], Token)).Jobs);
            var dataset = await new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, Token);
            var item = Assert.Single(dataset.Items);
            var values = await ReadFramesAsync(p, item.F0FeaturePath);
            Assert.Equal(enabled ? 220 : 0, values[4].Value, 8);
            Assert.Equal(enabled, dataset.AutoCorrectionEnabled);
            if (!enabled) Assert.Equal(Path.GetFullPath(source.F0FeaturePath, p.Workspace.RootPath), item.F0FeaturePath);
        }
    }

    [Theory]
    [InlineData("無音")]
    [InlineData("雑音")]
    public async Task PhysicalAudioMustSupportEstimatedPitch(string kind)
    {
        using var p = new TestProject();
        var source = await SeedPitchAsync(p, kind);
        var view = await p.Correction().BuildAsync(p.Workspace, true, Token);
        Assert.Equal(source.F0FeaturePath, Assert.Single(view.Segments).F0FeaturePath);
        var decision = Assert.Single(view.AppliedCorrections, x => x.Method == PitchGapInterpolator.Version);
        Assert.Equal(CorrectionApplicationStatus.Deferred, decision.ApplicationStatus);
        Assert.Empty(decision.ResultValues);
    }

    [Fact]
    public async Task RepeatedCompletionReusesIdenticalFilesAndFingerprint()
    {
        using var p = new TestProject();
        await SeedPitchAsync(p);
        var first = await p.Correction().BuildAsync(p.Workspace, true, Token);
        var path = Path.Combine(p.Workspace.RootPath, first.Segments[0].F0FeaturePath);
        var knownTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, knownTime);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = await p.Correction().BuildAsync(p.Workspace, true, Token);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.Equal(knownTime, File.GetLastWriteTimeUtc(path));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData("音声")]
    [InlineData("特徴")]
    [InlineData("設定")]
    public async Task InputOrSettingsChangeInvalidatesPlannedJob(string changed)
    {
        using var p = new TestProject();
        var source = await SeedPitchAsync(p);
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Singing }], Token)).Jobs);
        if (changed == "音声") WriteWave(Path.Combine(p.Workspace.RootPath, source.AudioPath), amplitude: 0.09);
        if (changed == "特徴")
        {
            var frames = await ReadFramesAsync(p, source.F0FeaturePath);
            frames[2] = frames[2] with { Value = 221 };
            await WriteFramesAsync(p, source.F0FeaturePath, frames);
        }
        if (changed == "設定")
            await new AutoCorrectionRepository().SaveAsync(p.Workspace,
                new() { PitchCompletion = new() { MaxGapSec = 0.04 } }, [], Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DiffSingerDatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, Token));
    }

    [Fact]
    public async Task CacheCorruptionIsNotSilentlyConsumedOrOverwritten()
    {
        using var p = new TestProject();
        var source = await SeedPitchAsync(p);
        var view = await p.Correction().BuildAsync(p.Workspace, true, Token);
        var path = Path.Combine(p.Workspace.RootPath, view.Segments[0].F0FeaturePath);
        await File.WriteAllTextAsync(path, "[]", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => p.Correction().BuildAsync(p.Workspace, true, Token));
        Assert.Equal("[]", await File.ReadAllTextAsync(path, Token));
        Assert.Equal(0, (await ReadFramesAsync(p, source.F0FeaturePath))[4].Value);
    }

    [Fact]
    public async Task InvalidInputDoesNotReplaceSavedCorrectionMetadata()
    {
        using var p = new TestProject();
        var source = await SeedPitchAsync(p);
        await p.Correction().BuildAsync(p.Workspace, false, Token);
        var path = Path.Combine(p.Workspace.MetadataPath, "auto-correction.json");
        var before = await File.ReadAllBytesAsync(path, Token);
        await File.WriteAllTextAsync(Path.Combine(p.Workspace.RootPath, source.F0FeaturePath), "[null]", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => p.Correction().BuildAsync(p.Workspace, true, Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.FeaturesPath, "completion")));
    }

    [Fact]
    public async Task CancellationDoesNotPublishCompletion()
    {
        using var p = new TestProject();
        await SeedPitchAsync(p);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.Correction().BuildAsync(p.Workspace, true, canceled.Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.FeaturesPath, "completion")));
    }

    [Fact]
    public async Task ManualExclusionIsNotReintroducedByCompletion()
    {
        using var p = new TestProject();
        await SeedPitchAsync(p);
        await new SpeakerOverrideService(p.Speakers).ExcludeSegmentAsync(p.Workspace, "song", true, Token);
        var view = await p.Correction().BuildAsync(p.Workspace, true, Token);
        Assert.Empty(view.Segments);
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.FeaturesPath, "completion")));
    }

    [Fact]
    public async Task MissingPhonemesAreTrackedSeparatelyForEachSpeaker()
    {
        using var p = new TestProject();
        var a = p.Segment("a");
        var b = p.Segment("b", "spk_b");
        b.Phonemes.Clear(); b.Phonemes.Add(new() { Phoneme = "i", StartSec = 0, EndSec = 0.3, Confidence = 0.95 });
        await p.SeedAsync(a, b);
        var view = await p.Correction().BuildAsync(p.Workspace, true, Token);
        var missingI = Assert.Single(view.AppliedCorrections, x => x.Method == "missing-phoneme-generation" && x.SpeakerId == "spk_a" && x.Phoneme == "i");
        Assert.Equal(CorrectionApplicationStatus.Deferred, missingI.ApplicationStatus);
        Assert.DoesNotContain(view.AppliedCorrections, x => x.Method == "missing-phoneme-generation" && x.SpeakerId == "spk_b" && x.Phoneme == "i");
    }

    private static async Task<UniversalVoiceSegment> SeedPitchAsync(TestProject p, string kind = "有声音")
    {
        var segment = p.Segment("song", type: SegmentContentType.Singing, duration: 0.3);
        await p.SeedAsync(segment);
        WriteWave(Path.Combine(p.Workspace.RootPath, segment.AudioPath), kind);
        await WriteFramesAsync(p, segment.F0FeaturePath, Frames(i => i is 4 or 5 ? 0 : 220, true));
        await WriteFramesAsync(p, segment.EnergyFeaturePath, Frames(_ => 0.05));
        await WriteFramesAsync(p, segment.VoicingFeaturePath, Frames(i => i is 4 or 5 ? 0 : 1));
        return segment;
    }

    private static Task WriteFramesAsync(TestProject p, string path, AudioFeatureFrame[] frames) =>
        File.WriteAllTextAsync(Path.GetFullPath(path, p.Workspace.RootPath), JsonSerializer.Serialize(frames, JsonOptions), Token);

    private static async Task<AudioFeatureFrame[]> ReadFramesAsync(TestProject p, string path) =>
        JsonSerializer.Deserialize<AudioFeatureFrame[]>(await File.ReadAllTextAsync(Path.GetFullPath(path, p.Workspace.RootPath), Token), JsonOptions)!;

    private static void WriteWave(string path, string kind = "有声音", double amplitude = 0.08)
    {
        const int rate = 48000, count = 14400;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(count * 2);
        var random = new Random(9);
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / rate;
            var value = amplitude * Math.Sin(2 * Math.PI * 220 * t);
            if (kind == "無音" && t >= 0.04 && t < 0.06) value = 0;
            if (kind == "雑音") value = amplitude * (random.NextDouble() * 2 - 1);
            writer.Write((short)(value * 32767));
        }
    }
}
