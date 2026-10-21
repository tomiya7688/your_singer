from __future__ import annotations

import unittest
from unittest.mock import patch

from your_singer_ml.universal_features import _align_phonemes


class UniversalFeatureAlignmentTests(unittest.TestCase):
    def test_uses_audio_word_boundaries_and_reports_proportional_method(self):
        units = [
            {"text": "ka", "start_sec": 0.4, "end_sec": 0.8, "confidence": 0.9},
            {"text": "mi", "start_sec": 1.1, "end_sec": 1.5, "confidence": 0.8},
        ]
        with patch("your_singer_ml.universal_features._phonemize", side_effect=lambda text: list(text)):
            phones, confidence, method = _align_phonemes(
                ["k", "a", "m", "i"], 2.0, 0.9, units
            )

        self.assertEqual("asr-word-boundary-proportional", method)
        self.assertEqual(["k", "a", "m", "i"], [item["phoneme"] for item in phones])
        self.assertAlmostEqual(0.4, phones[0]["start_sec"])
        self.assertAlmostEqual(0.6, phones[0]["end_sec"])
        self.assertAlmostEqual(1.3, phones[-1]["start_sec"])
        self.assertAlmostEqual(1.5, phones[-1]["end_sec"])
        self.assertAlmostEqual(0.9 * (0.9 + 0.8) / 2, confidence)

    def test_does_not_fabricate_alignment_when_asr_units_disagree_with_g2p(self):
        with patch("your_singer_ml.universal_features._phonemize", return_value=["a"]):
            phones, confidence, method = _align_phonemes(
                ["k", "a"], 1.0, 0.8,
                [{"text": "ka", "start_sec": 0.0, "end_sec": 1.0, "confidence": 0.9}],
            )

        self.assertEqual([], phones)
        self.assertEqual(0.0, confidence)
        self.assertEqual("unavailable", method)


if __name__ == "__main__":
    unittest.main()
