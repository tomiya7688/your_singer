import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

from your_singer_ml import style_bert_vits2_entry
from your_singer_ml.style_bert_vits2_session import StyleBertVits2TrainingSession
from your_singer_ml.style_bert_vits2_timbre import STYLE_DIM


# {
#   責務: [StyleBertVits2TimbreStageTests: 声質参照の複製・工程コマンド・スタイル追記の工程を確認する]
#   処理: [1: 合成のジョブを作る 2: 複製と拒否、埋め込み計算の注入、失敗の伝播を確認する]
# }
class StyleBertVits2TimbreStageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.dataset = self.root / "job"
        self.trainer_root = self.root / "bundle"
        self.dataset.mkdir()
        self.trainer_root.mkdir()
        self.wavs = []
        for name in ("a.wav", "b.wav"):
            path = self.root / name
            path.write_bytes(b"RIFF")
            self.wavs.append(str(path))

    def session(self, references, name="singing_reference"):
        return StyleBertVits2TrainingSession(
            dict(
                trainer_root=str(self.trainer_root),
                dataset_dir=str(self.dataset),
                output_dir=str(self.dataset / "trainer"),
                model_name="model",
                timbre_references=references,
                timbre_style_name=name,
            )
        )

    def test_references_are_copied_into_the_job_and_stage_command_is_fixed(self):
        session = self.session(self.wavs)
        session.output.mkdir()
        session.copy_timbre_references()
        self.assertEqual(
            [p.name for p in sorted(session.timbre_dir.iterdir())], ["0000_a.wav", "0001_b.wav"]
        )
        command = session.command("timbre_style")
        self.assertEqual(
            command[-3:], [str(session.timbre_dir), "singing_reference", str(session.assets)]
        )
        self.assertIn("timbre_style", command)

    def test_no_references_means_no_copy_and_no_directory(self):
        session = self.session([])
        session.output.mkdir()
        session.copy_timbre_references()
        self.assertFalse(session.timbre_dir.exists())

    def test_missing_or_non_wav_references_are_rejected_not_skipped(self):
        for bad in (str(self.root / "missing.wav"), str(self.root / "note.txt")):
            (self.root / "note.txt").write_text("x", encoding="utf-8")
            session = self.session([bad])
            session.output.mkdir(exist_ok=True)
            with self.assertRaises(ValueError):
                session.copy_timbre_references()
            if session.timbre_dir.exists():
                for item in session.timbre_dir.iterdir():
                    item.unlink()
                session.timbre_dir.rmdir()

    def test_invalid_reference_payload_is_rejected(self):
        for bad in ("a.wav", [1], [""]):
            with self.assertRaises(ValueError):
                self.session(bad)

    def test_append_timbre_style_uses_injected_embedding_and_writes_a_report(self):
        output = self.root / "trainer"
        references = output / "timbre_refs"
        model_dir = output / "model_assets" / "model"
        references.mkdir(parents=True)
        model_dir.mkdir(parents=True)
        np.save(model_dir / "style_vectors.npy", np.zeros((1, STYLE_DIM), dtype=np.float32))
        (model_dir / "config.json").write_text(
            json.dumps({"data": {"num_styles": 1, "style2id": {"Neutral": 0}}}), encoding="utf-8"
        )
        for name in ("0000_a.wav", "0001_b.wav", "0002_bad.wav"):
            (references / name).write_bytes(b"RIFF")

        def factory():
            def embed(path):
                if path.endswith("bad.wav"):
                    return np.ones(3)
                return np.full(STYLE_DIM, 1.0 if path.endswith("a.wav") else 3.0)

            return embed

        report = style_bert_vits2_entry.append_timbre_style(
            output, references, "singing_reference", model_dir, embed_factory=factory
        )
        self.assertEqual(report["reference_count"], 2)
        self.assertEqual(report["style_index"], 1)
        self.assertEqual([item["file"] for item in report["skipped"]], ["0002_bad.wav"])
        self.assertTrue(np.allclose(np.load(model_dir / "style_vectors.npy")[1], 2.0))
        self.assertEqual(
            json.loads((output / "timbre_style_report.json").read_text(encoding="utf-8"))[
                "style_name"
            ],
            "singing_reference",
        )

    def test_all_references_unusable_fails_instead_of_silently_skipping(self):
        output = self.root / "trainer"
        references = output / "timbre_refs"
        references.mkdir(parents=True)
        (references / "0000_a.wav").write_bytes(b"RIFF")
        with self.assertRaises(RuntimeError):
            style_bert_vits2_entry.append_timbre_style(
                output,
                references,
                "singing_reference",
                output / "model_assets" / "model",
                embed_factory=lambda: (lambda path: np.ones(3)),
            )
        with self.assertRaises(ValueError):
            style_bert_vits2_entry.append_timbre_style(
                output,
                output / "empty",
                "singing_reference",
                output,
                embed_factory=lambda: (lambda path: np.ones(3)),
            )


if __name__ == "__main__":
    unittest.main()
