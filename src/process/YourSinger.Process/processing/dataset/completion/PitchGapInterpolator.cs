using YourSinger.Data.Models;

namespace YourSinger.Process.Processing.Dataset.Completion;

public readonly record struct PitchEvidence(double Periodicity, double Rms);

public sealed record PitchGapDecision(int StartFrame, int EndFrameExclusive, bool Applied,
    string Reason, string? Phoneme, double Confidence, double[] OriginalValues, double[] ResultValues);

public sealed record PitchGapResult(AudioFeatureFrame[] F0, AudioFeatureFrame[] Voicing,
    IReadOnlyList<PitchGapDecision> Decisions);

/// <summary>短い欠損だけを対数周波数上で補間する。音素や音声そのものは生成しない。</summary>
public sealed class PitchGapInterpolator
{
    public const string Version = "short-vowel-gap-1";

    public static void ValidateSettings(PitchCompletionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Check(settings.MaxGapSec, 0.005, 0.12);
        Check(settings.MinAnchorConfidence, 0.60, 1.0);
        Check(settings.MaxBoundarySemitones, 0, 4.0);
        Check(settings.MinPeriodicity, 0.80, 1.0);
        Check(settings.MinRms, 0.001, 0.10);
        Check(settings.MinPhonemeConfidence, 0.50, 1.0);
        static void Check(double value, double min, double max)
        {
            if (!double.IsFinite(value) || value < min || value > max)
                throw new ArgumentOutOfRangeException(nameof(settings), "音高補完の設定値が許容範囲外です。");
        }
    }

    public PitchGapResult Complete(IReadOnlyList<AudioFeatureFrame> f0,
        IReadOnlyList<AudioFeatureFrame> energy, IReadOnlyList<AudioFeatureFrame> voicing,
        IReadOnlyList<PhonemeTimingRecord> phonemes, PitchCompletionSettings settings,
        Func<double, double, double, PitchEvidence> getEvidence,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        ArgumentNullException.ThrowIfNull(getEvidence);
        cancellationToken.ThrowIfCancellationRequested();
        var step = ValidateFrames(f0, energy, voicing);
        var output = f0.ToArray();
        var outputVoicing = voicing.ToArray();
        var decisions = new List<PitchGapDecision>();
        if (!settings.Enabled) return new(output, outputVoicing, decisions);

        for (var index = 0; index < f0.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NeedsEstimate(f0[index])) { index++; continue; }
            var start = index;
            while (index < f0.Count && NeedsEstimate(f0[index])) index++;
            var end = index;
            var before = f0.Skip(start).Take(end - start).Select(x => x.Value).ToArray();
            var reason = CheckBoundary(start, end, out var vowel);
            if (reason is not null)
            {
                decisions.Add(new(start, end, false, reason, vowel, 0, before, []));
                continue;
            }

            var left = f0[start - 1];
            var right = f0[end];
            var values = new double[end - start];
            var confidence = 0.65; // 校正済み確率ではない。観測値より低く保つ推定スコア。
            for (var i = start; i < end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fraction = (f0[i].TimeSec - left.TimeSec) / (right.TimeSec - left.TimeSec);
                var pitch = Math.Exp(Math.Log(left.Value) * (1 - fraction) + Math.Log(right.Value) * fraction);
                var evidence = getEvidence(f0[i].TimeSec, pitch, step);
                if (!double.IsFinite(evidence.Periodicity) || !double.IsFinite(evidence.Rms) ||
                    evidence.Periodicity < settings.MinPeriodicity || evidence.Rms < settings.MinRms)
                {
                    reason = "元音声の周期性または音量が不足しているため補完しません。";
                    break;
                }
                values[i - start] = pitch;
                confidence = Math.Min(confidence, Math.Min(left.Confidence!.Value, right.Confidence!.Value) * evidence.Periodicity * 0.75);
            }
            if (reason is not null)
            {
                decisions.Add(new(start, end, false, reason, vowel, 0, before, []));
                continue;
            }
            // 一つの欠損区間の全フレームが検証を通った場合だけ反映する。
            for (var i = start; i < end; i++)
            {
                output[i] = f0[i] with { Value = values[i - start], Confidence = confidence };
                outputVoicing[i] = voicing[i] with { Value = 1, Confidence = confidence };
            }
            decisions.Add(new(start, end, true, "同一母音内の短い欠損を、元音声の周期性を確認して補間しました。",
                vowel, confidence, before, values));
        }
        return new(output, outputVoicing, decisions);

        string? CheckBoundary(int start, int end, out string? vowel)
        {
            vowel = null;
            // 端点から外挿しない。さらに両側の一つ外の観測も確認する。
            if (start < 2 || end + 1 >= f0.Count)
                return "区間端または前後の観測不足のため補完しません。";
            if ((end - start) * step > settings.MaxGapSec + 1e-9)
                return "欠損が長いため補完しません。";
            foreach (var i in new[] { start - 2, start - 1, end, end + 1 })
                if (!IsAnchor(f0[i]) || voicing[i].Value < 0.5 || energy[i].Value < settings.MinRms)
                    return "前後の有声性または音高の信頼度が不足しています。";
            if (Semitones(f0[start - 1].Value, f0[end].Value) > settings.MaxBoundarySemitones ||
                Semitones(f0[start - 2].Value, f0[start - 1].Value) > 1.0 ||
                Semitones(f0[end].Value, f0[end + 1].Value) > 1.0)
                return "音高が大きく変化しているため補完しません。";
            var minEnergy = Math.Max(settings.MinRms, Math.Min(energy[start - 1].Value, energy[end].Value) * 0.25);
            for (var i = start; i < end; i++)
                if (energy[i].Value < minEnergy)
                    return "欠損区間に無音または大きな音量低下があるため補完しません。";
            var leftTime = f0[start - 2].TimeSec;
            var window = Math.Max(0.04, 3.0 / Math.Min(f0[start - 1].Value, f0[end].Value));
            var rightTime = f0[end + 1].TimeSec + window;
            var matches = phonemes.Where(p => p.Phoneme is "a" or "i" or "u" or "e" or "o")
                .Where(p => double.IsFinite(p.StartSec) && double.IsFinite(p.EndSec) &&
                    double.IsFinite(p.Confidence) && p.Confidence >= settings.MinPhonemeConfidence && p.Confidence <= 1 &&
                    p.StartSec >= 0 && p.StartSec <= leftTime && p.EndSec >= rightTime).ToArray();
            if (matches.Length != 1 || phonemes.Any(p => !ReferenceEquals(p, matches[0]) &&
                    p.StartSec < rightTime && p.EndSec > leftTime))
                return "一つの母音内と確認できないため補完しません。";
            vowel = matches[0].Phoneme;
            return null;
        }

        bool IsAnchor(AudioFeatureFrame frame) => frame.Value is >= 60 and <= 1200 &&
            frame.Confidence >= settings.MinAnchorConfidence;
    }

    private static bool NeedsEstimate(AudioFeatureFrame frame) => frame.Value == 0 || frame.Confidence < 0.45;
    private static double Semitones(double a, double b) => Math.Abs(12 * Math.Log2(a / b));

    private static double ValidateFrames(IReadOnlyList<AudioFeatureFrame> f0,
        IReadOnlyList<AudioFeatureFrame> energy, IReadOnlyList<AudioFeatureFrame> voicing)
    {
        if (f0.Count != energy.Count || f0.Count != voicing.Count || f0.Count > 200_000)
            throw new InvalidDataException("音高・音量・有声性のフレーム数が一致しないか、上限を超えています。");
        var step = f0.Count > 1 ? f0[1].TimeSec - f0[0].TimeSec : 0.01;
        if (!double.IsFinite(step) || step <= 0 || step > 0.02)
            throw new InvalidDataException("補完用フレームの時刻間隔が不正です。");
        for (var i = 0; i < f0.Count; i++)
        {
            foreach (var frame in new[] { f0[i], energy[i], voicing[i] })
                if (frame is null || !double.IsFinite(frame.TimeSec) || frame.TimeSec < 0 ||
                    !double.IsFinite(frame.Value) || frame.Value < 0 ||
                    (frame.Confidence is { } c && (!double.IsFinite(c) || c < 0 || c > 1)))
                    throw new InvalidDataException("補完用の特徴に不正な数値があります。");
            if (energy[i].Value > 1 || voicing[i].Value > 1 ||
                Math.Abs(f0[i].TimeSec - energy[i].TimeSec) > 1e-8 ||
                Math.Abs(f0[i].TimeSec - voicing[i].TimeSec) > 1e-8 ||
                (i > 0 && Math.Abs(f0[i].TimeSec - f0[i - 1].TimeSec - step) > 1e-8))
                throw new InvalidDataException("補完用の特徴の時刻または値域が不正です。");
        }
        return step;
    }
}
