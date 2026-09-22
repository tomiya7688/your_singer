using System.Security.Cryptography;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;

namespace YourSinger.Process.Processing.Dataset.Completion;

/// <summary>実際の補間結果を別ファイルに保存し、学習用の投影だけを差し替える。</summary>
public sealed class PitchFeatureCompletionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private const int MaxFeatureBytes = 32 * 1024 * 1024;

    public async Task<IReadOnlyList<CorrectionRecord>> ApplyAsync(ProjectWorkspace workspace,
        UniversalVoiceSegment segment, PitchCompletionSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PitchGapInterpolator.ValidateSettings(settings);
        if (!settings.Enabled || segment.ContentType != SegmentContentType.Singing) return [];
        if (string.IsNullOrWhiteSpace(segment.F0FeaturePath) || string.IsNullOrWhiteSpace(segment.EnergyFeaturePath) ||
            string.IsNullOrWhiteSpace(segment.VoicingFeaturePath))
            return [new CorrectionRecord
            {
                SegmentId = segment.SegmentId, SpeakerId = segment.SpeakerId,
                State = CorrectionState.WeakObserved, Method = PitchGapInterpolator.Version,
                ApplicationStatus = CorrectionApplicationStatus.Deferred,
                Reason = "必要な音高・音量・有声性の特徴がないため、音高補完は未適用です。"
            }];

        var originalF0Path = segment.F0FeaturePath;
        var f0 = await ReadFramesAsync(Path.GetFullPath(originalF0Path, workspace.RootPath), cancellationToken);
        var energy = await ReadFramesAsync(Path.GetFullPath(segment.EnergyFeaturePath, workspace.RootPath), cancellationToken);
        var voicing = await ReadFramesAsync(Path.GetFullPath(segment.VoicingFeaturePath, workspace.RootPath), cancellationToken);
        await using var wave = await Pcm16PeriodicityReader.OpenAsync(
            Path.GetFullPath(segment.AudioPath, workspace.RootPath), cancellationToken);
        var inputFingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            algorithm = PitchGapInterpolator.Version, settings, segment.SegmentId, segment.SourceId,
            segment.SpeakerId, segment.CacheKey, segment.Phonemes,
            audio = wave.ContentHash, f0 = f0.Hash, energy = energy.Hash, voicing = voicing.Hash
        }, JsonOptions));

        var completed = new PitchGapInterpolator().Complete(f0.Frames, energy.Frames, voicing.Frames,
            segment.Phonemes, settings, wave.Evaluate, cancellationToken);
        await wave.VerifyUnchangedAsync(cancellationToken);
        foreach (var input in new[] { f0, energy, voicing })
            if (Hash(await ReadLimitedBytesAsync(input.Path, cancellationToken)) != input.Hash)
                throw new IOException("補完処理中に特徴ファイルが変更されました。再試行してください。");

        string? correctedF0Path = null;
        if (completed.Decisions.Any(x => x.Applied))
        {
            var relativeRoot = $"features/completion/{PitchGapInterpolator.Version}/{inputFingerprint}";
            var root = Path.Combine(workspace.RootPath, relativeRoot);
            var f0Bytes = JsonSerializer.SerializeToUtf8Bytes(completed.F0, JsonOptions);
            var voicingBytes = JsonSerializer.SerializeToUtf8Bytes(completed.Voicing, JsonOptions);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["f0.json"] = f0Bytes,
                ["voicing.json"] = voicingBytes,
                ["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema_version = 1, algorithm = PitchGapInterpolator.Version, input_fingerprint = inputFingerprint,
                    f0_sha256 = Hash(f0Bytes), voicing_sha256 = Hash(voicingBytes),
                    estimated_frame_count = completed.Decisions.Where(x => x.Applied).Sum(x => x.EndFrameExclusive - x.StartFrame)
                }, JsonOptions)
            };
            await PublishAsync(root, files, cancellationToken);
            correctedF0Path = relativeRoot + "/f0.json";
            segment.F0FeaturePath = correctedF0Path;
            segment.VoicingFeaturePath = relativeRoot + "/voicing.json";
            UpdateSummaries(segment, completed);
        }

        var records = new List<CorrectionRecord>
        {
            new()
            {
                SegmentId = segment.SegmentId, SpeakerId = segment.SpeakerId, State = CorrectionState.Observed,
                Method = "pitch-input-snapshot", ApplicationStatus = CorrectionApplicationStatus.NotRequired,
                InputFingerprint = inputFingerprint, OriginalFeaturePath = originalF0Path,
                Reason = "補完の判定に使用した元音声・特徴・設定の識別子です。"
            }
        };
        records.AddRange(completed.Decisions.Select(decision => new CorrectionRecord
        {
            SegmentId = segment.SegmentId, SpeakerId = segment.SpeakerId, Phoneme = decision.Phoneme,
            State = decision.Applied
                ? decision.OriginalValues.All(x => x == 0) ? CorrectionState.Estimated : CorrectionState.Corrected
                : CorrectionState.WeakObserved,
            Method = PitchGapInterpolator.Version,
            ApplicationStatus = decision.Applied ? CorrectionApplicationStatus.Applied : CorrectionApplicationStatus.Deferred,
            Confidence = decision.Confidence, Reason = decision.Reason,
            StartFrame = decision.StartFrame, EndFrameExclusive = decision.EndFrameExclusive,
            OriginalValues = [.. decision.OriginalValues], ResultValues = [.. decision.ResultValues],
            OriginalFeaturePath = originalF0Path, ResultFeaturePath = decision.Applied ? correctedF0Path : null,
            InputFingerprint = inputFingerprint
        }));
        return records;
    }

    private static void UpdateSummaries(UniversalVoiceSegment segment, PitchGapResult completed)
    {
        var voiced = completed.F0.Where(x => x.Value > 0).Select(x => x.Value).Order().ToArray();
        segment.MeanF0Hz = voiced.Length == 0 ? 0 : voiced.Average();
        segment.F0StdDevHz = voiced.Length < 2 ? 0 : Math.Sqrt(voiced.Average(x => Math.Pow(x - segment.MeanF0Hz, 2)));
        segment.VoicedRatio = completed.Voicing.Length == 0 ? 0 : (double)completed.Voicing.Count(x => x.Value >= 0.5) / completed.Voicing.Length;
        if (voiced.Length >= 2)
        {
            var low = voiced[Math.Max(0, (int)(voiced.Length * 0.1) - 1)];
            var high = voiced[Math.Min(voiced.Length - 1, (int)(voiced.Length * 0.9))];
            segment.StyleProsody.PitchRangeSemitones = 12 * Math.Log2(high / low);
        }
        // PitchReliabilityは元の推定器の値を維持する。補完で観測信頼度を水増ししない。
    }

    private sealed record InputFrames(string Path, string Hash, AudioFeatureFrame[] Frames);

    private static async Task<InputFrames> ReadFramesAsync(string path, CancellationToken token)
    {
        var bytes = await ReadLimitedBytesAsync(path, token);
        try
        {
            var frames = JsonSerializer.Deserialize<AudioFeatureFrame[]>(bytes, JsonOptions)
                ?? throw new InvalidDataException("音高補完の特徴ファイルが空です。");
            if (frames.Any(x => x is null)) throw new InvalidDataException("音高補完の特徴に空のフレームがあります。");
            return new(path, Hash(bytes), frames);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"音高補完の時刻付き特徴を読み取れません: {Path.GetFileName(path)}", exception);
        }
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFeatureBytes) throw new InvalidDataException("音高補完の特徴ファイルがサイズ上限を超えています。");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static async Task PublishAsync(string destination, IReadOnlyDictionary<string, byte[]> files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Directory.Exists(destination))
        {
            await ValidateCacheAsync(destination, files, token);
            return;
        }
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".completion-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, bytes) in files)
                await File.WriteAllBytesAsync(Path.Combine(staging, name), bytes, token);
            token.ThrowIfCancellationRequested();
            try { Directory.Move(staging, destination); }
            catch (IOException) when (Directory.Exists(destination))
            {
                // 同時実行の結果は内容が一致する場合だけ再利用する。
                await ValidateCacheAsync(destination, files, token);
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task ValidateCacheAsync(string root, IReadOnlyDictionary<string, byte[]> files, CancellationToken token)
    {
        foreach (var (name, expected) in files)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path) || Hash(await ReadLimitedBytesAsync(path, token)) != Hash(expected))
                throw new InvalidDataException("保存済みの音高補完ファイルが壊れています。学習には使用しません。");
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
