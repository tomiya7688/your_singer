from __future__ import annotations

import array
import math
import random
import unittest

from your_singer_ml.audio_features import _estimate_pitch


# {
#   責務: [PitchAutocorrelationTests: FFT化後も有限区間の正規化相関を維持することを確認する]
#   処理: [1: スカラー参照実装と比較する 2: 無音・短い入力を確認する]
# }
class PitchAutocorrelationTests(unittest.TestCase):
    # {
    #   責務: [reference_pitch: 変更前の定義を独立したスカラー計算で表す]
    #   引数: [frame: PCMフレーム, sample_rate: サンプルレート]
    #   戻り値: [pitch: 相関最大の周波数, confidence: 正規化相関]
    # }
    @staticmethod
    def reference_pitch(frame: array.array, sample_rate: int) -> tuple[float | None, float]:
        mean = sum(frame) / len(frame)
        centered = [sample - mean for sample in frame]
        best_lag, best_correlation = 0, 0.0
        for lag in range(max(1, int(sample_rate / 1200)), min(len(frame) - 2, int(sample_rate / 60)) + 1):
            left, right = centered[:-lag], centered[lag:]
            denominator = math.sqrt(sum(sample * sample for sample in left) * sum(sample * sample for sample in right))
            correlation = sum(a * b for a, b in zip(left, right)) / denominator if denominator > 1e-9 else 0.0
            if correlation > best_correlation:
                best_lag, best_correlation = lag, correlation
        return ((sample_rate / best_lag) if best_lag and best_correlation >= 0.45 else None,
                max(0.0, min(1.0, best_correlation)))

    # {
    #   責務: [test_fft_matches_scalar_normalized_autocorrelation: 遅延ごとの正規化が元の定義と一致することを確認する]
    # }
    def test_fft_matches_scalar_normalized_autocorrelation(self):
        generator = random.Random(7)
        for sample_rate in (8000, 12000, 48000):
            frame = array.array("h", (int(2200 * math.sin(2 * math.pi * 233 * index / sample_rate)
                    + 700 * math.sin(2 * math.pi * 518 * index / sample_rate) + generator.uniform(-500, 500))
                    for index in range(int(sample_rate * 0.04))))
            with self.subTest(sample_rate=sample_rate):
                reference_pitch, reference_confidence = self.reference_pitch(frame, sample_rate)
                pitch, confidence = _estimate_pitch(frame, sample_rate)
                self.assertEqual(reference_pitch, pitch)
                self.assertAlmostEqual(reference_confidence, confidence, places=10)

    # {
    #   責務: [test_silence_short_and_invalid_rate_have_no_pitch: 検出できない入力へF0を割り当てない]
    # }
    def test_silence_short_and_invalid_rate_have_no_pitch(self):
        for frame, rate in ((array.array("h", [0] * 320), 8000), (array.array("h", [1000]), 48000),
                            (array.array("h", [1000] * 320), 0), (array.array("h", [1000] * 320), 8000)):
            with self.subTest(length=len(frame), rate=rate):
                self.assertEqual((None, 0.0), _estimate_pitch(frame, rate))


if __name__ == "__main__":
    unittest.main()
