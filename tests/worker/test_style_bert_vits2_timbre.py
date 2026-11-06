import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

from your_singer_ml.style_bert_vits2_timbre import (
    STYLE_DIM,
    TimbreStyleError,
    append_reference_style,
    compute_reference_vectors,
)


# {
#   責務: [StyleBertVits2TimbreTests: 歌唱由来の声質参照をスタイルとして追記する処理の入力検証と原子性を確認する]
#   処理: [1: 合成のモデルフォルダを作る 2: 追記・拒否・元ファイルの保持を確認する]
# }
class StyleBertVits2TimbreTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.neutral = np.zeros((1, STYLE_DIM), dtype=np.float32)
        np.save(self.root / "style_vectors.npy", self.neutral)
        config = {"data": {"num_styles": 1, "style2id": {"Neutral": 0}}}
        (self.root / "config.json").write_text(json.dumps(config), encoding="utf-8")

    def test_appends_mean_of_references_as_new_style(self):
        vectors = np.stack([np.full(STYLE_DIM, 1.0), np.full(STYLE_DIM, 3.0)])
        report = append_reference_style(self.root, "singing_reference", vectors)
        styles = np.load(self.root / "style_vectors.npy")
        config = json.loads((self.root / "config.json").read_text(encoding="utf-8"))
        self.assertEqual(styles.shape, (2, STYLE_DIM))
        self.assertTrue(np.allclose(styles[1], 2.0))
        self.assertTrue(np.allclose(styles[0], 0.0))
        self.assertEqual(config["data"]["style2id"], {"Neutral": 0, "singing_reference": 1})
        self.assertEqual(config["data"]["num_styles"], 2)
        self.assertEqual(report["style_index"], 1)
        self.assertEqual(report["reference_count"], 2)
        self.assertEqual(len(report["vectors_sha256"]), 64)

    def test_duplicate_name_and_bad_inputs_leave_files_unchanged(self):
        before = (
            (self.root / "style_vectors.npy").read_bytes(),
            (self.root / "config.json").read_bytes(),
        )
        good = np.ones((1, STYLE_DIM))
        bad_inputs = [
            ("Neutral", good),
            ("", good),
            ("a/b", good),
            ("x", np.empty((0, STYLE_DIM))),
            ("x", np.ones((1, STYLE_DIM - 1))),
            ("x", np.full((1, STYLE_DIM), np.nan)),
            ("x", np.full((1, STYLE_DIM), np.inf)),
        ]
        for name, vectors in bad_inputs:
            with self.assertRaises(TimbreStyleError):
                append_reference_style(self.root, name, vectors)
        self.assertEqual(
            before,
            (
                (self.root / "style_vectors.npy").read_bytes(),
                (self.root / "config.json").read_bytes(),
            ),
        )
        self.assertEqual(
            [p.name for p in self.root.iterdir()], ["config.json", "style_vectors.npy"]
        )

    def test_inconsistent_existing_files_are_rejected(self):
        config = {"data": {"num_styles": 3, "style2id": {"Neutral": 0, "Extra": 1}}}
        (self.root / "config.json").write_text(json.dumps(config), encoding="utf-8")
        with self.assertRaises(TimbreStyleError):
            append_reference_style(self.root, "singing_reference", np.ones((1, STYLE_DIM)))
        (self.root / "style_vectors.npy").unlink()
        with self.assertRaises(TimbreStyleError):
            append_reference_style(self.root, "singing_reference", np.ones((1, STYLE_DIM)))

    def test_compute_reference_vectors_skips_unusable_audio_with_reason(self):
        def embed(path):
            if path == "bad_dim.wav":
                return np.ones(10)
            if path == "nan.wav":
                return np.full(STYLE_DIM, np.nan)
            if path == "error.wav":
                raise RuntimeError("壊れた音声")
            return np.full(STYLE_DIM, 2.0)

        vectors, skipped = compute_reference_vectors(
            ["ok.wav", "bad_dim.wav", "nan.wav", "error.wav"], embed
        )
        self.assertEqual(vectors.shape, (1, STYLE_DIM))
        self.assertEqual([path for path, _ in skipped], ["bad_dim.wav", "nan.wav", "error.wav"])
        self.assertTrue(all(reason for _, reason in skipped))
        empty, skipped_all = compute_reference_vectors(["error.wav"], embed)
        self.assertEqual(empty.shape, (0, STYLE_DIM))
        self.assertEqual(len(skipped_all), 1)


if __name__ == "__main__":
    unittest.main()
