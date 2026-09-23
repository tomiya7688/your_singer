from __future__ import annotations

import array
import copy
import hashlib
import json
import math
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock
import wave

ROOT = Path(__file__).resolve().parents[2]
WORKER = ROOT / "src/process/YourSinger.Process/processing/ml/completion_worker"
sys.path.insert(0, str(WORKER))
from your_singer_completion.candidates import generate_candidates, choose_prompts, inspect_reference, _plain_path


class FixtureBackend:
    """制御フローの試験専用。話者を模したモデルや音素の生成ではない。"""
    def synthesize(self, text, reference, reference_text, seed):
        return [0.1 * math.sin(2 * math.pi * 220 * i / 24000) for i in range(24000)], 24000


class CandidateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        self.workspace = self.root / "日本語プロジェクト"
        self.workspace.mkdir()
        (self.workspace / "project.json").write_text("{}", encoding="utf-8")
        (self.workspace / "segments").mkdir()
        self.reference = self.workspace / "segments" / "ref.wav"
        self.write_reference()
        self.model = self.root / "model"
        self.model.mkdir()
        (self.model / "config.json").write_text("{}", encoding="utf-8")
        (self.model / "model.safetensors").write_bytes(b"file-integrity-fixture-not-a-model")
        self.payload = {
            "batch_id": "a" * 32, "speaker_id": "spk_a", "reference_segment_id": "ref",
            "reference_text": "これは確認用の文章です。", "snapshot_fingerprint": "b" * 64,
            "reference_sha256": hashlib.sha256(self.reference.read_bytes()).hexdigest(),
            "workspace_path": str(self.workspace), "reference_audio_path": str(self.reference),
            "model_directory": str(self.model), "missing_phonemes": ["k", "ky"],
        }
        self.factory = mock.Mock(return_value=FixtureBackend())

    def tearDown(self):
        self.temporary.cleanup()

    def write_reference(self, duration=3, amplitude=1000):
        samples = array.array("h", (int(amplitude * math.sin(2 * math.pi * 220 * i / 16000))
                                    for i in range(int(16000 * duration))))
        if sys.byteorder != "little": samples.byteswap()
        with wave.open(str(self.reference), "wb") as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(16000); wav.writeframes(samples.tobytes())

    def run_generation(self, factory=None):
        return generate_candidates(self.payload, backend_factory=factory or self.factory,
                                   phonemize=lambda text: ["k", "a"])

    def assert_not_published(self):
        root = self.workspace / "cache/phoneme-candidates"
        self.assertFalse((root / self.payload["batch_id"]).exists())
        self.assertFalse((root / (".pending-" + self.payload["batch_id"])).exists())

    def test_generation_is_separate_pending_speech_not_observed_coverage(self):
        before = self.reference.read_bytes()
        result = self.run_generation()
        manifest = json.loads((self.workspace / result["manifest_path"]).read_text(encoding="utf-8"))
        self.assertEqual(manifest["origin"], "synthetic")
        self.assertEqual(manifest["content_type"], "speech")
        self.assertFalse(manifest["training_eligible"])
        self.assertEqual(manifest["unplanned_phonemes"], ["ky"])
        item = manifest["items"][0]
        self.assertEqual(item["state"], "pending_review")
        self.assertEqual(item["phoneme_verification"], "not_performed")
        self.assertEqual(item["speaker_verification"], "not_performed")
        audio = self.workspace / item["audio_path"]
        self.assertEqual(hashlib.sha256(audio.read_bytes()).hexdigest(), item["sha256"])
        with wave.open(str(audio)) as wav:
            self.assertEqual((wav.getnchannels(), wav.getsampwidth(), wav.getframerate()), (1, 2, 24000))
        self.assertEqual(before, self.reference.read_bytes())
        self.assertEqual("{}", (self.workspace / "project.json").read_text())
        self.assertFalse((self.workspace / "metadata").exists())

    def test_existing_batch_is_never_overwritten(self):
        result = self.run_generation()
        manifest = self.workspace / result["manifest_path"]
        before = manifest.read_bytes()
        with self.assertRaises(FileExistsError): self.run_generation()
        self.assertEqual(manifest.read_bytes(), before)
        self.assertEqual(self.factory.call_count, 1)

    def test_reference_digest_mismatch_fails_before_loading_model(self):
        self.payload["reference_sha256"] = "c" * 64
        with self.assertRaises(ValueError): self.run_generation()
        self.factory.assert_not_called()
        self.assert_not_published()

    def test_model_missing_does_not_fall_back_to_remote_download(self):
        self.payload["model_directory"] = str(self.root / "missing")
        with self.assertRaises(FileNotFoundError): self.run_generation()
        self.factory.assert_not_called()
        self.assert_not_published()

    def test_relative_model_path_rejected(self):
        self.payload["model_directory"] = "Qwen/some-model"
        with self.assertRaises(ValueError): self.run_generation()
        self.factory.assert_not_called()

    def test_reference_outside_project_rejected(self):
        outside = self.root / "outside.wav"
        outside.write_bytes(self.reference.read_bytes())
        self.payload["reference_audio_path"] = str(outside)
        with self.assertRaises(ValueError): self.run_generation()
        self.factory.assert_not_called()

    def test_short_reference_rejected(self):
        self.write_reference(duration=1)
        self.payload["reference_sha256"] = hashlib.sha256(self.reference.read_bytes()).hexdigest()
        with self.assertRaises(ValueError): self.run_generation()
        self.factory.assert_not_called()

    def test_silent_reference_rejected(self):
        self.write_reference(amplitude=0)
        self.payload["reference_sha256"] = hashlib.sha256(self.reference.read_bytes()).hexdigest()
        with self.assertRaises(ValueError): self.run_generation()

    def test_truncated_reference_rejected(self):
        self.reference.write_bytes(self.reference.read_bytes()[:-10])
        with self.assertRaises(ValueError): inspect_reference(self.reference)

    def test_invalid_payloads_do_not_load_backend(self):
        invalid = [("batch_id", "../bad"), ("max_candidates", True), ("max_candidates", 9),
                   ("seed", -1), ("device", "shell"), ("missing_phonemes", []),
                   ("reference_text", "bad\ntext"), ("snapshot_fingerprint", "not-a-hash")]
        for key, value in invalid:
            with self.subTest(key=key, value=value):
                payload = copy.deepcopy(self.payload); payload[key] = value
                with self.assertRaises(ValueError):
                    generate_candidates(payload, backend_factory=self.factory, phonemize=lambda text: ["k"])
        self.factory.assert_not_called()

    def test_backend_failure_leaves_no_partial_publication(self):
        backend = mock.Mock()
        backend.synthesize.side_effect = RuntimeError("test failure")
        with self.assertRaises(RuntimeError): self.run_generation(mock.Mock(return_value=backend))
        self.assert_not_published()

    def test_invalid_outputs_are_rejected_and_cleaned(self):
        cases = [([0.0] * 24000, 24000), ([1.0] * 24000, 24000),
                 ([float("nan")] * 24000, 24000), ([0.1], 24000), ([0.1] * 24000, 1000)]
        for values, rate in cases:
            with self.subTest(value=values[0], size=len(values), rate=rate):
                backend = mock.Mock(); backend.synthesize.return_value = (values, rate)
                with self.assertRaises(ValueError): self.run_generation(mock.Mock(return_value=backend))
                self.assert_not_published()

    def test_input_change_during_generation_rejects_batch(self):
        reference = self.reference
        class Changing(FixtureBackend):
            def synthesize(self, *args):
                result = super().synthesize(*args)
                reference.write_bytes(reference.read_bytes() + b"changed")
                return result
        with self.assertRaises(ValueError): self.run_generation(lambda *args: Changing())
        self.assert_not_published()

    def test_model_change_during_generation_rejects_batch(self):
        model = self.model
        class Changing(FixtureBackend):
            def synthesize(self, *args):
                result = super().synthesize(*args)
                (model / "config.json").write_text('{"changed":true}')
                return result
        with self.assertRaises(ValueError): self.run_generation(lambda *args: Changing())
        self.assert_not_published()

    def test_symlink_is_not_followed(self):
        with mock.patch.object(Path, "is_symlink", return_value=True):
            with self.assertRaises(ValueError): _plain_path(self.reference)

    def test_keyboard_interrupt_removes_staging(self):
        backend = mock.Mock(); backend.synthesize.side_effect = KeyboardInterrupt()
        with self.assertRaises(KeyboardInterrupt): self.run_generation(mock.Mock(return_value=backend))
        self.assert_not_published()

    def test_unsupported_phone_does_not_invent_a_prompt(self):
        self.payload["missing_phonemes"] = [":"]
        with self.assertRaises(ValueError): self.run_generation()
        self.factory.assert_not_called()


class PlanningTests(unittest.TestCase):
    def test_prompt_selection_is_bounded_and_deterministic(self):
        def phones(text):
            return ["k"] if "海" in text else ["a"]
        a = choose_prompts(["k", "a", "ky"], phones, 1)
        b = choose_prompts(["ky", "a", "k"], phones, 1)
        self.assertEqual(a, b)
        self.assertEqual(len(a[0]), 1)
        self.assertEqual(a[1], ["a", "ky"])

    def test_unvoiced_vowels_count_as_vowels(self):
        prompts, remaining = choose_prompts(["i", "u"], lambda text: ["I", "U"], 2)
        self.assertEqual(remaining, [])
        self.assertEqual(prompts[0]["expected_phonemes"], ["I", "U"])

    def test_empty_g2p_is_error_not_character_fallback(self):
        with self.assertRaises(ValueError): choose_prompts(["k"], lambda text: [], 2)


class BackendContractTests(unittest.TestCase):
    def test_local_clone_api_uses_reference_text_japanese_and_limits(self):
        from your_singer_completion import qwen_backend
        torch = mock.MagicMock()
        torch.cuda.is_available.return_value = False
        api = mock.MagicMock()
        waveform = mock.Mock(ndim=1); waveform.tolist.return_value = [0.1, 0.2]
        api.from_pretrained.return_value.generate_voice_clone.return_value = ([waveform], 24000)
        with mock.patch.dict(sys.modules, {"torch": torch, "qwen_tts": mock.Mock(Qwen3TTSModel=api)}), \
             mock.patch.object(qwen_backend, "version", return_value="0.1.1"):
            backend = qwen_backend.QwenBackend(Path("/local/model"), "auto")
            values, rate = backend.synthesize("こんにちは", Path("/local/ref.wav"), "参照文", 123)
        kwargs = api.from_pretrained.call_args.kwargs
        self.assertTrue(kwargs["local_files_only"])
        self.assertTrue(kwargs["use_safetensors"])
        self.assertEqual(kwargs["device_map"], "cpu")
        call = api.from_pretrained.return_value.generate_voice_clone.call_args.kwargs
        self.assertEqual(call["language"], "Japanese")
        self.assertEqual(call["ref_text"], "参照文")
        self.assertFalse(call["x_vector_only_mode"])
        self.assertEqual(call["max_new_tokens"], 250)
        self.assertEqual((values, rate), ([0.1, 0.2], 24000))
        torch.manual_seed.assert_called_once_with(123)

    def test_missing_dictionary_never_calls_g2p(self):
        from your_singer_completion.qwen_backend import japanese_phonemes
        module = mock.Mock(OPEN_JTALK_DICT_DIR=b"/nonexistent/your-singer-dictionary")
        with mock.patch.dict(sys.modules, {"pyopenjtalk": module}):
            with self.assertRaises(FileNotFoundError): japanese_phonemes("こんにちは")
        module.g2p.assert_not_called()


class ProtocolTests(unittest.TestCase):
    def test_command_errors_are_json_without_model_dependencies(self):
        for request in ("{", "[]", json.dumps({"request_id": "r1", "command": "unsupported"})):
            with self.subTest(request=request):
                environment = dict(os.environ, PYTHONPATH=str(WORKER), PYTHONIOENCODING="utf-8")
                result = subprocess.run([sys.executable, "-m", "your_singer_completion"],
                                        input=request + "\n", capture_output=True, text=True, encoding="utf-8",
                                        env=environment, timeout=10)
                self.assertEqual(result.returncode, 0)
                response = json.loads(result.stdout)
                self.assertEqual(response["status"], "error")
                self.assertEqual(len(result.stdout.splitlines()), 1)

    def test_empty_input_emits_nothing(self):
        result = subprocess.run([sys.executable, "-m", "your_singer_completion"], input="",
                                capture_output=True, text=True, env=dict(os.environ, PYTHONPATH=str(WORKER)), timeout=10)
        self.assertEqual(result.stdout, "")


if __name__ == "__main__":
    unittest.main()
