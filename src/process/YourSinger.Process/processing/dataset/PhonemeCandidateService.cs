using System.Security.Cryptography;
using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Data.Repositories;
using YourSinger.Process.Processing.Ml.Bridge;

namespace YourSinger.Process.Processing.Dataset;

public sealed record PhonemeCandidatePlan(string SpeakerId, string ReferenceSegmentId,
    string ReferenceText, string ReferenceAudioPath, string ReferenceSha256,
    string SnapshotFingerprint, IReadOnlyList<string> MissingPhonemes);

public sealed record PhonemeCandidateResult(string BatchId, string ManifestPath, int CandidateCount);

/// <summary>不足音素の発話候補を生成する。採用・学習・観測カバレッジの更新は行わない。</summary>
public sealed class PhonemeCandidateService(UniversalVoiceDatasetRepository repository,
    IPhonemeCandidateWorker worker)
{
    public async Task<PhonemeCandidatePlan> PlanAsync(ProjectWorkspace workspace, string speakerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(speakerId)) throw new ArgumentException("話者を選択してください。", nameof(speakerId));
        var snapshot = await new TrainingDatasetSnapshotService(repository).LoadAsync(workspace, cancellationToken);
        var segments = snapshot.Segments.Where(x => x.SpeakerId == speakerId &&
            x.ContentType == SegmentContentType.Speech && double.IsFinite(x.AsrConfidence) &&
            double.IsFinite(x.AlignmentConfidence) && x.AsrConfidence >= 0.75 && x.AlignmentConfidence >= 0.65)
            .OrderByDescending(x => x.AsrConfidence).ThenBy(x => x.SegmentId, StringComparer.Ordinal).ToArray();
        if (segments.Length == 0)
            throw new InvalidOperationException("この話者の参照に使える会話区間がありません。分類・除外・認識結果を確認してください。");
        var observed = segments.SelectMany(x => x.Phonemes)
            .Where(x => double.IsFinite(x.Confidence) && x.Confidence >= 0.65)
            .Select(x => x.Phoneme is "I" or "U" ? x.Phoneme.ToLowerInvariant() : x.Phoneme)
            .ToHashSet(StringComparer.Ordinal);
        var expected = JapaneseCoverageCatalog.CorePhonemes.Concat(JapaneseCoverageCatalog.Palatalized)
            .Concat(JapaneseCoverageCatalog.Foreign).Distinct(StringComparer.Ordinal);
        var missing = expected.Where(x => !observed.Contains(x)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0) throw new InvalidOperationException("この話者には今回の対象音素の不足がありません。");
        var reference = segments.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Transcript) &&
            x.Transcript.Length <= 500 && x.Phonemes.Count > 0 &&
            x.Phonemes.Max(p => p.EndSec) is >= 3 and <= 15);
        if (reference is null)
            throw new InvalidOperationException("参照には文章が確認できる3〜15秒の会話区間が必要です。");
        var path = Path.GetFullPath(reference.AudioPath, workspace.RootPath);
        var relative = Path.GetRelativePath(workspace.SegmentsPath, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("参照にはプロジェクト内の観測区間の音声を使用してください。");
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        return new(speakerId, reference.SegmentId, reference.Transcript, path, hash,
            TrainingDatasetSnapshotService.CreateFingerprint(snapshot, false, []), missing);
    }

    public async Task<PhonemeCandidateResult> GenerateAsync(ProjectWorkspace workspace,
        PhonemeCandidatePlan confirmedPlan, CancellationToken cancellationToken = default)
    {
        // 確認画面を開いた後の除外・分類・文字起こし・参照波形の変更を取り込まない。
        var current = await PlanAsync(workspace, confirmedPlan.SpeakerId, cancellationToken);
        if (current.SnapshotFingerprint != confirmedPlan.SnapshotFingerprint ||
            current.ReferenceSegmentId != confirmedPlan.ReferenceSegmentId ||
            current.ReferenceSha256 != confirmedPlan.ReferenceSha256)
            throw new InvalidOperationException("参照データが変更されています。補完候補の画面を開き直してください。");
        var batchId = Guid.NewGuid().ToString("N");
        var response = await worker.GenerateAsync(new
        {
            batch_id = batchId, workspace_path = workspace.RootPath,
            speaker_id = current.SpeakerId, reference_segment_id = current.ReferenceSegmentId,
            reference_audio_path = current.ReferenceAudioPath, reference_sha256 = current.ReferenceSha256,
            reference_text = current.ReferenceText, snapshot_fingerprint = current.SnapshotFingerprint,
            missing_phonemes = current.MissingPhonemes, max_candidates = 4, seed = 42, device = "auto",
            model_directory = Path.Combine(AppContext.BaseDirectory, "models", "phoneme-completion", "qwen3-tts-base")
        }, cancellationToken);
        var expectedRelative = $"cache/phoneme-candidates/{batchId}/manifest.json";
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("batch_id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != batchId ||
            !response.TryGetProperty("manifest_path", out var manifest) || manifest.ValueKind != JsonValueKind.String || manifest.GetString() != expectedRelative ||
            !response.TryGetProperty("candidate_count", out var count) || !count.TryGetInt32(out var total) || total is < 1 or > 4 ||
            !response.TryGetProperty("training_eligible", out var eligible) || eligible.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("補完候補ワーカーから不正な結果が返されました。");
        var after = await PlanAsync(workspace, confirmedPlan.SpeakerId, cancellationToken);
        if (after.SnapshotFingerprint != current.SnapshotFingerprint || after.ReferenceSha256 != current.ReferenceSha256)
            throw new InvalidOperationException("生成中に参照データが変更されました。保存された候補は未採用のままです。");
        var fullPath = Path.Combine(workspace.RootPath, expectedRelative);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("補完候補の記録が見つかりません。", fullPath);
        return new(batchId, fullPath, total);
    }
}
