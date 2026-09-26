using System.Security.Cryptography;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Process.Processing.Dataset.Completion;
using YourSinger.Process.Processing.Talk;

namespace YourSinger.Process.Tests;

// MLの生成結果と検証結果はテスト用に明示する。実話者の合成品質は検証しない。
public sealed class PhonemeSupplementTests
{
    private static CancellationToken Token => TestProject.Token;
    private static PhonemeSupplementService Service(bool passed = true) => new(async (_, payload, token) =>
    {
        var input = JsonSerializer.SerializeToElement(payload);
        var path = input.GetProperty("output_path").GetString()!;
        TestProject.WriteWave(path);
        var reference = input.GetProperty("reference_audio_path").GetString()!;
        var counts = input.GetProperty("observed_phoneme_counts").EnumerateObject()
            .ToDictionary(x => x.Name, x => x.Value.GetInt32(), StringComparer.Ordinal);
        var expected = new[] { "k", "a" };
        var assisted = expected.Distinct(StringComparer.Ordinal)
            .Where(x => counts.GetValueOrDefault(x) < PhonemeSupplementService.AssistThreshold)
            .Order(StringComparer.Ordinal).ToArray();
        var missing = assisted.Where(x => counts.GetValueOrDefault(x) == 0).Order(StringComparer.Ordinal).ToArray();
        var sparse = assisted.Where(x => counts.GetValueOrDefault(x) is > 0 and < PhonemeSupplementService.AssistThreshold)
            .Order(StringComparer.Ordinal).ToArray();
        return JsonSerializer.SerializeToElement(new
        {
            generator_version = PhonemeSupplementService.GeneratorVersion,
            model_fingerprint = new string('a', 64),
            reference_sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(reference, token))),
            audio_sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, token))),
            recognized_text = "か", expected_phonemes = expected, recognized_phonemes = expected,
            missing_phonemes = missing, sparse_phonemes = sparse, assisted_phonemes = assisted,
            speaker_similarity = 0.95, asr_avg_logprob = -0.1,
            no_speech_probability = 0.01, duration_sec = 3, rms = 0.1, clipping_ratio = 0,
            silence_ratio = 0.1, machine_passed = passed, reasons = passed ? Array.Empty<string>() : ["検証不合格"]
        });
    });
    private static Task<PhonemeSupplementCandidate> Generate(TestProject p, PhonemeSupplementService s) =>
        s.GenerateAsync(p.Workspace, "spk_a", "reference", p.Workspace.RootPath, "話者A", "か", Token);

    [Fact]
    public async Task GeneratedCandidateIsNotAutomaticallyUsed()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference"));
        var candidate = await Generate(p, Service());
        Assert.True(candidate.Verification.MachinePassed);
        Assert.Single((await p.Correction().BuildAsync(p.Workspace, true, Token)).Segments);
        Assert.Single(await Service().ListAsync(p.Workspace, Token));
    }

    [Fact]
    public async Task AcceptedSpeechReachesTalkBuilderButDoesNotRewriteObservation()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference"));
        var originalPath = Path.Combine(p.Workspace.MetadataPath, "universal-voice-dataset.json");
        var original = await File.ReadAllBytesAsync(originalPath, Token);
        var service = Service(); var candidate = await Generate(p, service);
        await service.SetAcceptedAsync(p.Workspace, candidate.CandidateId, true, Token);
        var view = await p.Correction().BuildAsync(p.Workspace, true, Token);
        var generated = Assert.Single(view.Segments, x => x.SourceId.StartsWith("generated:", StringComparison.Ordinal));
        Assert.Equal(SegmentContentType.Speech, generated.ContentType);
        Assert.Empty(generated.Phonemes); Assert.Equal(0, generated.AlignmentConfidence);
        Assert.Contains(view.AppliedCorrections, x => x.SegmentId == generated.SegmentId && x.ApplicationStatus == CorrectionApplicationStatus.Applied);
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk }], Token)).Jobs);
        var built = await new StyleBertVits2DatasetBuilder(p.DatasetRepository).BuildAsync(p.Workspace, job, "話者A", Token);
        Assert.Equal(2, built.Items.Count);
        Assert.Contains(built.Items, x => x.SegmentId == generated.SegmentId && File.Exists(x.AudioPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(originalPath, Token));
    }

    [Fact]
    public async Task OffDoesNotUseAcceptedCandidate()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference"));
        var service = Service(); var candidate = await Generate(p, service);
        await service.SetAcceptedAsync(p.Workspace, candidate.CandidateId, true, Token);
        Assert.Single((await p.Correction().BuildAsync(p.Workspace, false, Token)).Segments);
        Assert.Equal(2, (await p.Correction().BuildAsync(p.Workspace, true, Token)).Segments.Count);
    }

    [Fact]
    public async Task SparseOnlyPhonemeCanBeSupplementedWithoutMissingPhoneme()
    {
        using var p = new TestProject();
        var reference = p.Segment("reference");
        reference.Phonemes.Add(new() { Phoneme = "k", StartSec = 0, EndSec = 0.2, Confidence = 0.95 });
        reference.Phonemes.Add(new() { Phoneme = "k", StartSec = 0.2, EndSec = 0.4, Confidence = 0.95 });
        reference.Phonemes.Add(new() { Phoneme = "k", StartSec = 0.4, EndSec = 0.6, Confidence = 0.95 });
        await p.SeedAsync(reference);

        var service = Service();
        var candidate = await Generate(p, service);

        Assert.Empty(candidate.Verification.MissingPhonemes);
        Assert.Equal(new[] { "a" }, candidate.Verification.SparsePhonemes);
        Assert.Equal(new[] { "a" }, candidate.Verification.AssistedPhonemes);

        await service.SetAcceptedAsync(p.Workspace, candidate.CandidateId, true, Token);
        var view = await p.Correction().BuildAsync(p.Workspace, true, Token);
        Assert.Contains(view.Segments, x => x.SegmentId == "generated_" + candidate.CandidateId);
        Assert.Contains(view.AppliedCorrections, x =>
            x.Method == "sparse-phoneme-generation" &&
            x.Phoneme == "a" &&
            x.ApplicationStatus == CorrectionApplicationStatus.Deferred);
        Assert.Contains(view.AppliedCorrections, x =>
            x.Method == PhonemeSupplementService.GeneratorVersion &&
            x.Reason!.Contains("補助対象: a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SingingObservationsDoNotSatisfyTalkSparseThreshold()
    {
        using var p = new TestProject();
        var speech = p.Segment("reference");
        speech.Phonemes.Add(new() { Phoneme = "k", StartSec = 0, EndSec = 0.2, Confidence = 0.95 });
        speech.Phonemes.Add(new() { Phoneme = "k", StartSec = 0.2, EndSec = 0.4, Confidence = 0.95 });
        speech.Phonemes.Add(new() { Phoneme = "k", StartSec = 0.4, EndSec = 0.6, Confidence = 0.95 });
        var singing = p.Segment("song", type: SegmentContentType.Singing);
        singing.Phonemes.Clear();
        singing.Phonemes.Add(new() { Phoneme = "a", StartSec = 0, EndSec = 0.4, Confidence = 0.95 });
        singing.Phonemes.Add(new() { Phoneme = "a", StartSec = 0.4, EndSec = 0.8, Confidence = 0.95 });
        singing.Phonemes.Add(new() { Phoneme = "a", StartSec = 0.8, EndSec = 1.2, Confidence = 0.95 });
        await p.SeedAsync(speech, singing);

        var counts = PhonemeSupplementService.ObservedPhonemeCounts(
            await new TrainingDatasetSnapshotService(p.DatasetRepository).LoadAsync(p.Workspace, Token),
            "spk_a");

        Assert.Equal(1, counts["a"]);
        Assert.Equal(3, counts["k"]);

        var service = Service();
        var candidate = await Generate(p, service);
        Assert.Empty(candidate.Verification.MissingPhonemes);
        Assert.Equal(new[] { "a" }, candidate.Verification.SparsePhonemes);
    }

    [Fact]
    public async Task FailedMachineValidationCannotBeAccepted()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference"));
        var service = Service(false); var candidate = await Generate(p, service);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SetAcceptedAsync(p.Workspace, candidate.CandidateId, true, Token));
        Assert.Single((await p.Correction().BuildAsync(p.Workspace, true, Token)).Segments);
    }

    [Fact]
    public async Task DamagedCandidateCannotBeAccepted()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference"));
        var service = Service(); var candidate = await Generate(p, service);
        await File.WriteAllTextAsync(Path.Combine(p.Workspace.RootPath, candidate.AudioPath), "破損", Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SetAcceptedAsync(p.Workspace, candidate.CandidateId, true, Token));
    }

    [Fact]
    public async Task ObservationEditInvalidatesAcceptedCandidateButCanBeRevoked()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference"));
        var service = Service(); var candidate = await Generate(p, service);
        await service.SetAcceptedAsync(p.Workspace, candidate.CandidateId, true, Token);
        var dataset = (await p.DatasetRepository.LoadAsync(p.Workspace, Token))!;
        dataset.Segments[0].Transcript = "変更後";
        await p.DatasetRepository.SaveAsync(p.Workspace, dataset, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.Correction().BuildAsync(p.Workspace, true, Token));
        Assert.Single((await p.Correction().BuildAsync(p.Workspace, false, Token)).Segments);
        await service.SetAcceptedAsync(p.Workspace, candidate.CandidateId, false, Token);
        Assert.Single((await p.Correction().BuildAsync(p.Workspace, true, Token)).Segments);
    }

    [Fact]
    public async Task OnlyOneCandidatePerSpeakerIsAdopted()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference"));
        var service = Service(); var first = await Generate(p, service); var second = await Generate(p, service);
        await service.SetAcceptedAsync(p.Workspace, first.CandidateId, true, Token);
        await service.SetAcceptedAsync(p.Workspace, second.CandidateId, true, Token);
        var view = await p.Correction().BuildAsync(p.Workspace, true, Token);
        Assert.Equal(2, view.Segments.Count);
        Assert.Contains(view.Segments, x => x.SegmentId == "generated_" + second.CandidateId);
        Assert.DoesNotContain(view.Segments, x => x.SegmentId == "generated_" + first.CandidateId);
    }

    [Fact]
    public async Task AcceptedCandidateInvalidatesAlreadyPlannedOnJob()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference"));
        var job = Assert.Single((await p.Planner().CreateBatchAsync(p.Workspace,
            [new() { SpeakerId = "spk_a", Target = TrainingTarget.Talk }], Token)).Jobs);
        var service = Service(); var candidate = await Generate(p, service);
        await service.SetAcceptedAsync(p.Workspace, candidate.CandidateId, true, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StyleBertVits2DatasetBuilder(p.DatasetRepository)
            .BuildAsync(p.Workspace, job, "話者A", Token));
    }

    [Fact]
    public async Task FailureRemovesIncompleteCandidateDirectory()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference"));
        var service = new PhonemeSupplementService((_, _, _) => throw new InvalidOperationException("ワーカー異常"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(p, service));
        Assert.Empty(Directory.GetDirectories(Path.Combine(p.Workspace.FeaturesPath, "phoneme-supplements")));
    }

    [Fact]
    public async Task AnotherSpeakersReferenceIsRejectedBeforeWorker()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference", "spk_b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(p, Service()));
    }

    [Fact]
    public async Task CancellationBeforeGenerationDoesNotCreateCandidate()
    {
        using var p = new TestProject(); await p.SeedAsync(p.Segment("reference"));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().GenerateAsync(p.Workspace,
            "spk_a", "reference", p.Workspace.RootPath, "話者A", "か", canceled.Token));
        Assert.False(Directory.Exists(Path.Combine(p.Workspace.FeaturesPath, "phoneme-supplements")));
    }
}
