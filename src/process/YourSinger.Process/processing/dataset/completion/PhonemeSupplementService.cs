using System.Security.Cryptography;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Dataset.Completion;

public sealed class PhonemeSupplementService
{
    public const string GeneratorVersion = "speaker-phoneme-candidate-2";
    public const string LegacyGeneratorVersion = "speaker-phoneme-candidate-1";
    public const int MinimumObservedCount = 3;
    private static readonly SemaphoreSlim SelectionLock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly Func<string, object, CancellationToken, Task<JsonElement>> _send;

    public PhonemeSupplementService(Func<string, object, CancellationToken, Task<JsonElement>>? send = null) =>
        _send = send ?? ((command, payload, token) => new MlWorkerClient().SendAsync(command, payload, token));

    public async Task<PhonemeSupplementRuntimeStatus> CheckRuntimeAsync(
        bool fullVerify = false,
        CancellationToken cancellationToken = default)
    {
        var response = await _send("phoneme_supplement_preflight", new
        {
            resources_root = Path.Combine(
                AppContext.BaseDirectory,
                "workers",
                "models",
                "phoneme-supplement"),
            full_verify = fullVerify
        }, cancellationToken);

        return response.Deserialize<PhonemeSupplementRuntimeStatus>(Json)
            ?? throw new InvalidDataException("音素補完runtimeの確認結果がありません。");
    }

    public static IEnumerable<UniversalVoiceSegment> References(UniversalVoiceDatasetRecord dataset, string speakerId) =>
        dataset.Segments.Where(x => x.SpeakerId == speakerId && x.ContentType == SegmentContentType.Speech &&
            x.AsrConfidence >= 0.65 && x.AlignmentConfidence >= 0.65);

    public static Dictionary<string, int> ReliablePhonemeCounts(UniversalVoiceDatasetRecord dataset, string speakerId) =>
        References(dataset, speakerId)
            .SelectMany(x => x.Phonemes)
            .Where(x => x.Confidence >= 0.65 && !string.IsNullOrWhiteSpace(x.Phoneme))
            .Select(x => x.Phoneme is "I" or "U" ? x.Phoneme.ToLowerInvariant() : x.Phoneme)
            .GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

    public static IReadOnlyList<string> SupplementTargets(UniversalVoiceDatasetRecord dataset, string speakerId)
    {
        var counts = ReliablePhonemeCounts(dataset, speakerId);
        return JapaneseCoverageCatalog.CorePhonemes
            .Concat(JapaneseCoverageCatalog.Palatalized)
            .Concat(JapaneseCoverageCatalog.Foreign)
            .Distinct(StringComparer.Ordinal)
            .Where(x => counts.GetValueOrDefault(x) < MinimumObservedCount)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static string ObservationFingerprint(UniversalVoiceDatasetRecord dataset, string speakerId) =>
        ObservationFingerprint(dataset, speakerId, GeneratorVersion);

    private static string ObservationFingerprint(
        UniversalVoiceDatasetRecord dataset,
        string speakerId,
        string generatorVersion) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = generatorVersion, dataset.SchemaVersion, dataset.StageVersion, speakerId,
            segments = References(dataset, speakerId).OrderBy(x => x.SegmentId, StringComparer.Ordinal).ToArray()
        })));

    public async Task<PhonemeSupplementCandidate> GenerateAsync(ProjectWorkspace workspace, string speakerId,
        string referenceSegmentId, string modelDirectory, string modelSpeaker, string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 120 || text.IndexOfAny(['\r', '\n', '|']) >= 0)
            throw new ArgumentException("生成する文章は改行を含まない120文字以内にしてください。", nameof(text));
        var snapshot = await LoadSnapshotAsync(workspace, cancellationToken);
        var source = References(snapshot, speakerId).SingleOrDefault(x => x.SegmentId == referenceSegmentId)
            ?? throw new InvalidOperationException("参照用の会話区間がありません。手動除外・話者・信頼度を確認してください。");
        var fingerprint = ObservationFingerprint(snapshot, speakerId);
        var counts = ReliablePhonemeCounts(snapshot, speakerId);
        var supplementTargets = SupplementTargets(snapshot, speakerId);
        if (supplementTargets.Count == 0)
            throw new InvalidOperationException("この話者には未観測または少量の補助対象音素がありません。");
        var referencePath = Path.GetFullPath(source.AudioPath, workspace.RootPath);
        var referenceHash = await HashAsync(referencePath, cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var relativeAudio = $"features/phoneme-supplements/{id}/candidate.wav";
        var directory = Path.Combine(workspace.RootPath, "features", "phoneme-supplements", id);
        Directory.CreateDirectory(directory);
        try
        {
            var response = await _send("generate_phoneme_candidate", new
            {
                text, model_speaker = modelSpeaker, model_directory = Path.GetFullPath(modelDirectory),
                reference_audio_path = referencePath, output_path = Path.Combine(directory, "candidate.wav"),
                observed_phonemes = counts.Keys.Order(StringComparer.Ordinal).ToArray(),
                target_phonemes = supplementTargets,
                resources_root = Path.Combine(AppContext.BaseDirectory, "workers", "models", "phoneme-supplement")
            }, cancellationToken);
            var verification = response.Deserialize<PhonemeSupplementVerification>(Json)
                ?? throw new InvalidDataException("補完候補の検証結果がありません。");
            if (referenceHash != verification.ReferenceSha256 ||
                referenceHash != await HashAsync(referencePath, cancellationToken) ||
                verification.AudioSha256 != await HashAsync(Path.Combine(directory, "candidate.wav"), cancellationToken) ||
                fingerprint != ObservationFingerprint(await LoadSnapshotAsync(workspace, cancellationToken), speakerId))
                throw new InvalidDataException("補完処理中に入力が変わりました。候補は採用しません。");
            var targets = verification.TargetPhonemes.Count > 0
                ? verification.TargetPhonemes
                : verification.MissingPhonemes;
            if (targets.Count == 0 || targets.Any(x => counts.GetValueOrDefault(x) >= MinimumObservedCount))
                throw new InvalidDataException("生成結果に現在の補助対象ではない音素が含まれています。");
            var candidate = new PhonemeSupplementCandidate
            {
                CandidateId = id, SpeakerId = speakerId, ReferenceSegmentId = referenceSegmentId,
                ModelDirectory = Path.GetFullPath(modelDirectory), ModelSpeaker = modelSpeaker, Text = text,
                ObservationFingerprint = fingerprint, AudioPath = relativeAudio,
                TargetPhonemes = targets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                ObservedCountByPhoneme = targets.Distinct(StringComparer.Ordinal)
                    .ToDictionary(x => x, x => counts.GetValueOrDefault(x), StringComparer.Ordinal),
                Verification = verification
            };
            await WriteAsync(Path.Combine(directory, "candidate.json"), candidate, cancellationToken);
            return candidate;
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public async Task<PhonemeSupplementCandidate> LoadAsync(ProjectWorkspace workspace, string candidateId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(candidateId, "N", out _)) throw new InvalidDataException("補完候補のIDが不正です。");
        var path = Path.Combine(workspace.RootPath, "features", "phoneme-supplements", candidateId, "candidate.json");
        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer.DeserializeAsync<PhonemeSupplementCandidate>(stream, Json, cancellationToken)
            ?? throw new InvalidDataException("補完候補の保存データが空です。");
        if (value.CandidateId != candidateId || value.AudioPath != $"features/phoneme-supplements/{candidateId}/candidate.wav")
            throw new InvalidDataException("補完候補の保存先が一致しません。");
        return value;
    }

    public async Task<IReadOnlyList<PhonemeSupplementCandidate>> ListAsync(ProjectWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(workspace.RootPath, "features", "phoneme-supplements");
        var result = new List<PhonemeSupplementCandidate>();
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(path, "candidate.json")))
                result.Add(await LoadAsync(workspace, Path.GetFileName(path), cancellationToken));
        }
        return result;
    }

    public async Task SetAcceptedAsync(ProjectWorkspace workspace, string candidateId, bool accepted,
        CancellationToken cancellationToken = default)
    {
        await SelectionLock.WaitAsync(cancellationToken);
        try
        {
            var candidate = await LoadAsync(workspace, candidateId, cancellationToken);
            var selection = await LoadSelectionAsync(workspace, cancellationToken);
            if (accepted)
            {
                await ValidateAsync(workspace, await LoadSnapshotAsync(workspace, cancellationToken), candidate, cancellationToken);
                selection.CandidateBySpeaker[candidate.SpeakerId] = candidateId;
            }
            else if (selection.CandidateBySpeaker.GetValueOrDefault(candidate.SpeakerId) == candidateId)
                selection.CandidateBySpeaker.Remove(candidate.SpeakerId);
            await WriteAsync(SelectionPath(workspace), selection, cancellationToken);
        }
        finally { SelectionLock.Release(); }
    }

    public async Task<IReadOnlyList<CorrectionRecord>> AppendAcceptedAsync(ProjectWorkspace workspace,
        UniversalVoiceDatasetRecord dataset, CancellationToken cancellationToken = default)
    {
        var selection = await LoadSelectionAsync(workspace, cancellationToken);
        var result = new List<CorrectionRecord>();
        // すべて検証した後に投影へ追加する。生成音声を次の候補の観測根拠にしない。
        var candidates = new List<PhonemeSupplementCandidate>();
        foreach (var (speaker, id) in selection.CandidateBySpeaker.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var candidate = await LoadAsync(workspace, id, cancellationToken);
            if (candidate.SpeakerId != speaker) throw new InvalidDataException("補完候補の話者が一致しません。");
            await ValidateAsync(workspace, dataset, candidate, cancellationToken);
            candidates.Add(candidate);
        }
        foreach (var candidate in candidates)
        {
            var segmentId = "generated_" + candidate.CandidateId;
            if (dataset.Segments.Any(x => x.SegmentId == segmentId)) throw new InvalidDataException("生成区間IDが重複しています。");
            dataset.Segments.Add(new UniversalVoiceSegment
            {
                SegmentId = segmentId, SourceId = "generated:" + candidate.CandidateId,
                SpeakerId = candidate.SpeakerId, ContentType = SegmentContentType.Speech,
                AudioPath = candidate.AudioPath, CacheKey = candidate.Verification.AudioSha256,
                Transcript = candidate.Text
                // 音素の時刻・観測ASR信頼度・F0を捏造しない。トーク前処理が本文から音素を作る。
            });
            var effectiveTargets = candidate.TargetPhonemes.Count > 0
                ? candidate.TargetPhonemes
                : candidate.Verification.MissingPhonemes;
            var currentCounts = ReliablePhonemeCounts(dataset, candidate.SpeakerId);
            foreach (var phoneme in effectiveTargets)
            {
                var observedCount = candidate.ObservedCountByPhoneme.Count > 0
                    ? candidate.ObservedCountByPhoneme.GetValueOrDefault(phoneme)
                    : currentCounts.GetValueOrDefault(phoneme);
                result.Add(new CorrectionRecord
                {
                    SegmentId = segmentId, SpeakerId = candidate.SpeakerId, Phoneme = phoneme,
                    State = CorrectionState.Estimated,
                    Method = observedCount == 0 ? "missing-phoneme-generation" : "sparse-phoneme-generation",
                    ApplicationStatus = CorrectionApplicationStatus.Applied,
                    Confidence = candidate.Verification.SpeakerSimilarity,
                    OriginalValue = observedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    InputFingerprint = candidate.ObservationFingerprint, ResultFeaturePath = candidate.AudioPath,
                    CorrectedValue = candidate.Verification.AudioSha256,
                    Reason = observedCount == 0
                        ? "未観測音素を含む、機械検証・利用者確認済みの生成会話です。観測回数には加算しません。"
                        : $"観測{observedCount}回の少量音素を補助する、機械検証・利用者確認済みの生成会話です。観測回数には加算しません。"
                });
            }
        }
        return result;
    }

    private static async Task ValidateAsync(ProjectWorkspace workspace, UniversalVoiceDatasetRecord snapshot,
        PhonemeSupplementCandidate candidate, CancellationToken token)
    {
        var v = candidate.Verification;
        var legacy = v.GeneratorVersion == LegacyGeneratorVersion;
        var targets = candidate.TargetPhonemes.Count > 0 ? candidate.TargetPhonemes : v.MissingPhonemes;
        var counts = ReliablePhonemeCounts(snapshot, candidate.SpeakerId);
        if ((!legacy && v.GeneratorVersion != GeneratorVersion) || !v.MachinePassed || v.Reasons.Count != 0 ||
            v.ExpectedPhonemes.Count == 0 || !v.ExpectedPhonemes.SequenceEqual(v.RecognizedPhonemes, StringComparer.Ordinal) ||
            targets.Count == 0 || targets.Any(x => !v.ExpectedPhonemes.Contains(x, StringComparer.Ordinal)) ||
            targets.Any(x => counts.GetValueOrDefault(x) >= MinimumObservedCount) ||
            v.MissingPhonemes.Any(x => counts.GetValueOrDefault(x) != 0 || !targets.Contains(x, StringComparer.Ordinal)) ||
            !double.IsFinite(v.AsrAvgLogprob) || v.AsrAvgLogprob < -0.50 || v.AsrAvgLogprob > 0 ||
            !InRange(v.SpeakerSimilarity, 0.80, 1) || !InRange(v.NoSpeechProbability, 0, 0.20) ||
            !InRange(v.DurationSec, 2, 14) || !InRange(v.Rms, 0.008, 1) ||
            !InRange(v.ClippingRatio, 0, 0.001) || !InRange(v.SilenceRatio, 0, 0.60) ||
            !IsHash(v.ModelFingerprint) || !IsHash(v.ReferenceSha256) || !IsHash(v.AudioSha256))
            throw new InvalidDataException("この補完候補は検証条件を満たしません。学習には採用できません。");
        if (candidate.ObservedCountByPhoneme.Count > 0 &&
            candidate.ObservedCountByPhoneme.Any(x =>
                !targets.Contains(x.Key, StringComparer.Ordinal) || counts.GetValueOrDefault(x.Key) != x.Value))
            throw new InvalidOperationException("補助対象音素の観測回数が変わっています。候補を作り直してください。");
        var expectedObservationFingerprint = ObservationFingerprint(
            snapshot,
            candidate.SpeakerId,
            legacy ? LegacyGeneratorVersion : GeneratorVersion);
        if (candidate.ObservationFingerprint != expectedObservationFingerprint)
            throw new InvalidOperationException("観測データや手動編集が変わっています。補完候補を作り直すか、採用を解除してください。");
        var reference = References(snapshot, candidate.SpeakerId).SingleOrDefault(x => x.SegmentId == candidate.ReferenceSegmentId)
            ?? throw new InvalidDataException("補完の参照区間がありません。");
        if (await HashAsync(Path.GetFullPath(reference.AudioPath, workspace.RootPath), token) != v.ReferenceSha256 ||
            await HashAsync(Path.Combine(workspace.RootPath, candidate.AudioPath), token) != v.AudioSha256)
            throw new InvalidDataException("参照音声または補完音声が変更・破損しています。");
    }

    private static bool InRange(double x, double min, double max) => double.IsFinite(x) && x >= min && x <= max;
    private static bool IsHash(string x) => x.Length == 64 && x.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static Task<UniversalVoiceDatasetRecord> LoadSnapshotAsync(ProjectWorkspace workspace, CancellationToken token) =>
        new TrainingDatasetSnapshotService(new UniversalVoiceDatasetRepository()).LoadAsync(workspace, token);
    private static string SelectionPath(ProjectWorkspace workspace) => Path.Combine(workspace.MetadataPath, "phoneme-supplements.json");
    private static async Task<PhonemeSupplementSelection> LoadSelectionAsync(ProjectWorkspace workspace, CancellationToken token)
    {
        if (!File.Exists(SelectionPath(workspace))) return new();
        await using var stream = File.OpenRead(SelectionPath(workspace));
        return await JsonSerializer.DeserializeAsync<PhonemeSupplementSelection>(stream, Json, token)
            ?? throw new InvalidDataException("補完候補の選択データが不正です。");
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
    private static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, Json, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
