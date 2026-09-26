import contextlib
import tempfile
import types
import unittest
import wave
from pathlib import Path
from unittest.mock import patch

from your_singer_ml import transcript_correction as tc


class FakeModel:
    def __init__(self, values):
        self.values = values

    def transcribe(self, path, **kwargs):
        text, logprob = self.values[kwargs["beam_size"]]
        return iter([types.SimpleNamespace(text=text, avg_logprob=logprob)]), None


class TranscriptCorrectionWorkerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.audio = self.root / "input.wav"
        with wave.open(str(self.audio), "wb") as writer:
            writer.setnchannels(1)
            writer.setsampwidth(2)
            writer.setframerate(16000)
            writer.writeframes(b"\x00\x00" * 32000)

    def tearDown(self):
        self.temp.cleanup()

    def payload(self, observed="かき", confidence=0.45):
        return {
            "audio_path": str(self.audio),
            "observed_transcript": observed,
            "observed_confidence": confidence,
            "resources_root": str(self.root / "resources"),
        }

    @patch.object(tc, "_phones", side_effect=lambda text: list(text))
    def test_two_decoders_agree_and_improve(self, _):
        model = FakeModel({5: ("かぎ", -0.20), 1: ("かぎ", -0.25)})
        result = tc.review_transcript(self.payload(), lambda: model)
        self.assertTrue(result["machine_passed"])
        self.assertTrue(result["changed"])
        self.assertEqual("かぎ", result["candidate_transcript"])
        self.assertGreaterEqual(result["confidence"], 0.72)

    @patch.object(tc, "_phones", side_effect=lambda text: list(text))
    def test_decoder_disagreement_is_not_applied(self, _):
        model = FakeModel({5: ("かぎ", -0.20), 1: ("かき", -0.20)})
        result = tc.review_transcript(self.payload(), lambda: model)
        self.assertFalse(result["machine_passed"])
        self.assertTrue(any("一致" in reason for reason in result["reasons"]))

    @patch.object(tc, "_phones", side_effect=lambda text: list(text))
    def test_small_confidence_improvement_is_not_applied(self, _):
        model = FakeModel({5: ("かぎ", -0.55), 1: ("かぎ", -0.55)})
        result = tc.review_transcript(self.payload(confidence=0.65), lambda: model)
        self.assertFalse(result["machine_passed"])
        self.assertTrue(any("改善" in reason for reason in result["reasons"]))

    @patch.object(tc, "_phones", side_effect=lambda text: list(text))
    def test_same_transcript_is_not_recorded_as_correction(self, _):
        model = FakeModel({5: ("かき", -0.20), 1: ("かき", -0.20)})
        result = tc.review_transcript(self.payload(), lambda: model)
        self.assertFalse(result["machine_passed"])
        self.assertFalse(result["changed"])
        self.assertTrue(any("同等" in reason for reason in result["reasons"]))

    def test_missing_local_asr_model_fails_closed(self):
        with self.assertRaises(FileNotFoundError):
            tc.review_transcript(self.payload())


if __name__ == "__main__":
    unittest.main()
