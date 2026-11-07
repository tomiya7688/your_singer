using System.Text.Json;
using YourSinger.Data.Models;
using YourSinger.Data.Processing;
using YourSinger.Process.Processing.Dataset.Completion;

namespace YourSinger.Process.Processing.Dataset;

// {
//   責務: [MfaMeasurementValidator: 実測記録・音声・segment音素が同じ測定に属することを検証する]
//   処理: [1: 参照と記録hashを確認する 2: 音声hashと長さを照合する 3: 音素時刻と測定条件を照合する]
// }
internal static class MfaMeasurementValidator
{
    private const string ModelHash = "85928ffb1024486872a677a92e1fa94d2f997462d0b84c73a58aa9bb0e35179a";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // { 責務: [CanReuseAsync: 旧・欠落・不正な測定をキャッシュから再利用しない] }
    internal static async Task<bool> CanReuseAsync(ProjectWorkspace workspace, UniversalVoiceSegment segment, CancellationToken token)
    {
        try { await ValidateFeatureAsync(workspace, segment, token); return true; }
        catch (InvalidDataException) { return false; }
        catch (EndOfStreamException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (JsonException) { return false; }
    }

    // { 責務: [ValidateFeatureAsync: 実測方式と参照の整合を検証し、異常を明示して拒否する] }
    internal static async Task ValidateFeatureAsync(ProjectWorkspace workspace, UniversalVoiceSegment segment, CancellationToken token)
    {
        var reference = segment.AlignmentMeasurement;
        if (segment.AlignmentMethod != UniversalVoiceSegment.RequiredSingingTrainingAlignmentMethod)
        {
            if (reference is not null) throw new InvalidDataException("推定結果にMFA測定参照を付けられません。");
            return;
        }
        if (reference is null || !segment.HasValidSingingTrainingAlignment())
            throw new InvalidDataException("MFAの測定記録または音素時刻がありません。再解析してください。");
        if (!IsHash(reference.Sha256) || !IsHash(reference.AudioSha256))
            throw new InvalidDataException("MFA測定参照のSHA-256が不正です。");

        // { 処理: [読んだ同じバイト列のhashを検査してからJSONへ変換する] }
        var bytes = await MfaMeasurementFiles.ReadRecordAsync(workspace, reference.Path, token);
        if (MfaMeasurementFiles.Hash(bytes) != reference.Sha256)
            throw new InvalidDataException("MFA測定記録が変更されました。再解析してください。");
        var record = JsonSerializer.Deserialize<MeasurementRecord>(bytes, Json)
            ?? throw new InvalidDataException("MFA測定記録が空です。");
        ValidateRecord(record, segment, reference);

        var audioStream = MfaMeasurementFiles.OpenRead(workspace, segment.AudioPath, MfaMeasurementFiles.MaxAudioBytes);
        await using var audio = await Pcm16PeriodicityReader.OpenAsync(audioStream, token, MfaMeasurementFiles.MaxAudioBytes);
        if (audio.ContentHash != reference.AudioSha256 || Math.Abs(audio.DurationSec - record.DurationSec) > 1e-6)
            throw new InvalidDataException("MFA測定時と音声の内容または長さが一致しません。再解析してください。");
    }

    // { 責務: [ValidateRecord: 保存条件と音素時刻が現在のsegmentと一致することを確認する] }
    private static void ValidateRecord(MeasurementRecord record, UniversalVoiceSegment segment, AlignmentMeasurementReference reference)
    {
        if (record.SchemaVersion != 1 || record.AudioSha256 != reference.AudioSha256 ||
            !double.IsFinite(record.DurationSec) || record.DurationSec <= 0 ||
            record.Conditions?.AlignmentMethod != UniversalVoiceSegment.RequiredSingingTrainingAlignmentMethod ||
            record.Conditions.MfaVersion != "3.4.2" || record.Conditions.ModelSha256 != ModelHash ||
            record.ConfidenceCalibrated != false || record.BreathClassificationRun != false ||
            record.Phonemes is null || record.Phonemes.Count != segment.Phonemes.Count)
            throw new InvalidDataException("MFA測定条件または音素列が一致しません。");
        ValidateIntervals(record, segment);
        for (var i = 0; i < segment.Phonemes.Count; i++)
        {
            var expected = segment.Phonemes[i]; var actual = record.Phonemes[i];
            if (actual is null || actual.Phoneme != expected.Phoneme || actual.StartSec != expected.StartSec ||
                actual.EndSec != expected.EndSec || actual.Confidence != expected.Confidence || actual.EndSec > record.DurationSec)
                throw new InvalidDataException("MFA測定記録と音素時刻が一致しません。");
        }
    }

    // { 責務: [ValidateIntervals: 元のMFA区間と期待音素のラベル・境界・時間順を照合する] }
    private static void ValidateIntervals(MeasurementRecord record, UniversalVoiceSegment segment)
    {
        if (record.MfaIntervals is not { Count: > 0 }) throw new InvalidDataException("MFAの元区間がありません。");
        var previousEnd = 0.0; var phoneIndex = 0;
        foreach (var interval in record.MfaIntervals)
        {
            if (interval is null || interval.Label is null || !interval.StartSec.HasValue || !interval.EndSec.HasValue ||
                !double.IsFinite(interval.StartSec.Value) || !double.IsFinite(interval.EndSec.Value) ||
                interval.StartSec.Value < previousEnd - 1e-6 || interval.EndSec.Value <= interval.StartSec.Value ||
                interval.EndSec.Value > record.DurationSec)
                throw new InvalidDataException("MFAの元区間の時刻または順序が不正です。");
            previousEnd = interval.EndSec.Value;
            if (interval.Label is "" or "sil") continue;
            if (phoneIndex >= segment.Phonemes.Count) throw new InvalidDataException("MFAの音素数が一致しません。");
            var phone = segment.Phonemes[phoneIndex++];
            if (MfaPhoneMap.Encode(phone.Phoneme) != interval.Label ||
                phone.StartSec != interval.StartSec || phone.EndSec != interval.EndSec)
                throw new InvalidDataException("MFAの元ラベルと音素時刻が一致しません。");
        }
        if (phoneIndex != segment.Phonemes.Count) throw new InvalidDataException("MFAの音素数が一致しません。");
    }

    // { 責務: [IsHash: SHA-256が小文字hex 64文字であるか確認する] }
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // { 責務: [MeasurementRecord: workerが保存した検証対象の記録を読み取る] }
    private sealed class MeasurementRecord
    {
        public int SchemaVersion { get; init; }
        public string? AudioSha256 { get; init; }
        public double DurationSec { get; init; }
        public MeasurementConditions? Conditions { get; init; }
        public List<MeasurementPhone?>? Phonemes { get; init; }
        public List<MeasurementInterval>? MfaIntervals { get; init; }
        public bool? ConfidenceCalibrated { get; init; }
        public bool? BreathClassificationRun { get; init; }
    }

    // { 責務: [MeasurementPhone: 内部音素と必須の実測値を読み取る] }
    private sealed class MeasurementPhone
    {
        public string? Phoneme { get; init; }
        public double? StartSec { get; init; }
        public double? EndSec { get; init; }
        public double? Confidence { get; init; }
    }

    // { 責務: [MeasurementInterval: MFA phones tierの時刻と元ラベルを読み取る] }
    private sealed class MeasurementInterval
    {
        public double? StartSec { get; init; }
        public double? EndSec { get; init; }
        public string? Label { get; init; }
    }

    // { 責務: [MeasurementConditions: 音素アラインメントの固定資源を読み取る] }
    private sealed class MeasurementConditions
    {
        public string? AlignmentMethod { get; init; }
        public string? MfaVersion { get; init; }
        public string? ModelSha256 { get; init; }
    }
}
