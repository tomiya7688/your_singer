import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from your_singer_ml.style_bert_vits2_session import PRETRAINED_NAMES, StyleBertVits2TrainingSession


# {
#   責務: [StyleBertVits2InitializationTests: 学習モード(fine_tune / from_scratch)と初期重みの契約を、起動前に確認する]
#   補足: [合成資源での確認。実GPU・実際のTrainerによる初期重みの読込は未検証]
# }
class StyleBertVits2InitializationTests(unittest.TestCase):
    def setUp(self):
        bundle_check = patch(
            "your_singer_ml.style_bert_vits2_session.StyleBertVits2Bundle.validate"
        )
        bundle_check.start()
        self.addCleanup(bundle_check.stop)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.trainer, self.dataset = root / "bundle", root / "job"
        required = (
            "runtime/python.exe",
            "resample.py",
            "preprocess_text.py",
            "bert_gen.py",
            "style_gen.py",
            "train_ms.py",
            "default_config.yml",
            "configs/config.json",
            "style_bert_vits2/constants.py",
        )
        for relative in required:
            path = self.trainer / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic-test-resource")
        self.weights = {}
        for name in PRETRAINED_NAMES:
            path = self.trainer / "pretrained" / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode())
            self.weights[name] = hashlib.sha256(name.encode()).hexdigest()
        (self.dataset / "raw").mkdir(parents=True)
        audio = self.dataset / "raw" / "sample.wav"
        audio.write_bytes(b"synthetic-test-audio")
        record = dict(
            job_id="job",
            dataset_fingerprint="fp",
            auto_correction_enabled=False,
            items=[dict(audio_path=str(audio), speaker_name="話者", transcript="テスト")],
        )
        (self.dataset / "dataset.json").write_text(
            json.dumps(record, ensure_ascii=False), encoding="utf-8"
        )
        (self.dataset / "esd.list").write_text("sample.wav|話者|JP|テスト\n", encoding="utf-8")
        self.payload = dict(
            trainer_root=str(self.trainer),
            dataset_dir=str(self.dataset),
            output_dir=str(self.dataset / "trainer"),
            model_name="話者モデル",
            job_id="job",
            dataset_fingerprint="fp",
            auto_correction_enabled=False,
        )

    def fine_tune(self, **overrides):
        components = [dict(name=name, sha256=sha, path="p") for name, sha in self.weights.items()]
        return dict(
            self.payload,
            resolved_training_mode="fine_tune",
            initialization=dict(type="pretrained_checkpoint", components=components),
            **overrides,
        )

    def test_fine_tune_with_matching_hashes_prepares_and_records_the_mode(self):
        session = StyleBertVits2TrainingSession(self.fine_tune())
        session.prepare()
        request = json.loads((session.output / "request.json").read_text(encoding="utf-8"))
        self.assertEqual(request["resolved_training_mode"], "fine_tune")

    def test_fine_tune_with_a_different_hash_or_missing_component_fails_before_output(self):
        payload = self.fine_tune()
        payload["initialization"]["components"][0]["sha256"] = "0" * 64
        with self.assertRaises(ValueError):
            StyleBertVits2TrainingSession(payload).prepare()
        self.assertFalse((self.dataset / "trainer").exists())
        payload = self.fine_tune()
        payload["initialization"]["components"].pop()
        with self.assertRaises(ValueError):
            StyleBertVits2TrainingSession(payload).prepare()

    def test_fine_tune_never_falls_back_to_scratch_when_the_pretrained_weight_is_missing(self):
        (self.trainer / "pretrained" / PRETRAINED_NAMES[0]).unlink()
        with self.assertRaises(FileNotFoundError):
            StyleBertVits2TrainingSession(self.fine_tune()).prepare()

    def test_from_scratch_does_not_need_pretrained_weights_but_rejects_any_specified_ones(self):
        for name in PRETRAINED_NAMES:
            (self.trainer / "pretrained" / name).unlink()
        scratch = dict(
            self.payload,
            resolved_training_mode="from_scratch",
            initialization=dict(type="random_initialization", components=[]),
        )
        StyleBertVits2TrainingSession(scratch).prepare()
        with self.assertRaises(ValueError):
            StyleBertVits2TrainingSession(
                dict(
                    scratch,
                    output_dir=str(self.dataset / "trainer"),
                    initialization=dict(
                        type="random_initialization",
                        components=[dict(name="G_0.safetensors", sha256="0" * 64, path="p")],
                    ),
                )
            ).prepare()

    def test_unknown_mode_is_rejected(self):
        with self.assertRaises(ValueError):
            StyleBertVits2TrainingSession(dict(self.payload, resolved_training_mode="auto"))

    def test_stage_report_describes_the_weights_actually_placed(self):
        session = StyleBertVits2TrainingSession(self.fine_tune())
        session.prepare()
        models = session.data / "models"
        models.mkdir()
        for name in PRETRAINED_NAMES:
            (models / name).write_bytes(name.encode())
        report = session.stage_report()
        self.assertEqual(report["type"], "pretrained_checkpoint")
        self.assertEqual(
            {item["name"]: item["sha256"] for item in report["components"]}, self.weights
        )
        (models / PRETRAINED_NAMES[0]).unlink()
        with self.assertRaises(RuntimeError):
            session.stage_report()

    def test_stage_report_for_scratch_fails_when_a_weight_was_placed(self):
        scratch = dict(
            self.payload,
            resolved_training_mode="from_scratch",
            initialization=dict(type="random_initialization", components=[]),
        )
        session = StyleBertVits2TrainingSession(scratch)
        session.prepare()
        models = session.data / "models"
        models.mkdir()
        self.assertEqual(session.stage_report(), dict(type="random_initialization", components=[]))
        (models / PRETRAINED_NAMES[1]).write_bytes(b"x")
        with self.assertRaises(RuntimeError):
            session.stage_report()

    def test_legacy_payload_without_a_mode_keeps_the_existing_behaviour(self):
        session = StyleBertVits2TrainingSession(self.payload)
        session.prepare()
        self.assertIsNone(session.stage_report())


if __name__ == "__main__":
    unittest.main()
