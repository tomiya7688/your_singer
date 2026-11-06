import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from your_singer_ml import diffsinger_binarize as binarize
from your_singer_ml import diffsinger_weighted_sampler as sampler


# {
#   責務: [DiffSingerBinarizeTests: 学習設定の内容・検証分割・起動引数・item名記録の確認・失敗の記録を確認する]
#   補足: [公式binarizationの本体は実行しない(代替の実行関数)。実モデルでの実行は未検証]
# }
class DiffSingerBinarizeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.bundle = root / "bundle"
        (self.bundle / "runtime").mkdir(parents=True)
        (self.bundle / "runtime" / "python.exe").write_bytes(b"py")
        (self.bundle / "source" / "scripts").mkdir(parents=True)
        (self.bundle / "source" / "scripts" / "binarize.py").write_bytes(b"x")
        self.project = root / "project"
        self.job = self.project / "training" / "diffsinger" / "job-1"
        acoustic = self.job / "acoustic-input"
        acoustic.mkdir(parents=True)
        self.names = [f"segment_{i}" for i in range(12)]
        (acoustic / "transcriptions.csv").write_text(
            "name,ph_seq,ph_dur\n" + "".join(f"{n},a,1.0\n" for n in self.names), encoding="utf-8"
        )
        (acoustic / "dictionary-ja.txt").write_text("a\ta\n", encoding="utf-8")
        self.payload = dict(
            trainer_root=str(self.bundle),
            project_root=str(self.project),
            job_id="job-1",
            speaker_id="spk_a",
        )

    def fake_run(self, record_names=True, code=0):
        def run(command, **kwargs):
            self.command = command
            if record_names:
                binary = self.job / "binary"
                binary.mkdir(exist_ok=True)
                held = set(binarize.choose_test_prefixes(self.names))
                train = [n for n in self.names if n not in held]
                (binary / sampler.NAMES_FILE).write_text(
                    json.dumps(dict(names=train)), encoding="utf-8"
                )
            return SimpleNamespace(returncode=code)

        return run

    def test_validation_split_is_deterministic_and_keeps_training_items(self):
        self.assertEqual(
            binarize.choose_test_prefixes(self.names),
            binarize.choose_test_prefixes(self.names[::-1]),
        )
        self.assertEqual(binarize.choose_test_prefixes(["only"]), [])
        two = binarize.choose_test_prefixes(["a", "b"])
        self.assertEqual(len(two), 1)
        self.assertLessEqual(
            len(binarize.choose_test_prefixes([str(i) for i in range(500)])), binarize.MAX_VALID
        )

    def test_config_and_official_entry_are_used_and_names_are_verified(self):
        with patch(
            "your_singer_ml.diffsinger_binarize.run_reclaiming", side_effect=self.fake_run()
        ):
            result = binarize.binarize_diffsinger(self.payload)
        config = Path(result["config_path"]).read_text(encoding="utf-8")
        self.assertIn("acoustic.yaml", config)
        self.assertIn("test_prefixes:", config)
        self.assertIn("enabled: false", config)
        self.assertTrue(self.command[1].endswith("diffsinger_binarize_entry.py"))
        self.assertEqual(self.command[3:5], ["--config", result["config_path"]])
        self.assertEqual(result["item_count"], 12 - len(result["test_prefixes"]))

    def test_missing_name_record_or_nonzero_exit_fails_and_leaves_a_failure_marker(self):
        with patch(
            "your_singer_ml.diffsinger_binarize.run_reclaiming",
            side_effect=self.fake_run(record_names=False),
        ):
            with self.assertRaises(ValueError):
                binarize.binarize_diffsinger(self.payload)
        self.assertEqual(
            json.loads((self.job / "run_failure.json").read_text(encoding="utf-8"))["kind"],
            "failed",
        )
        (self.job / "acoustic-training.yaml").unlink()
        (self.job / "run_failure.json").unlink()
        with patch(
            "your_singer_ml.diffsinger_binarize.run_reclaiming", side_effect=self.fake_run(code=3)
        ):
            with self.assertRaisesRegex(RuntimeError, "終了コード3"):
                binarize.binarize_diffsinger(self.payload)

    def test_an_existing_config_is_not_overwritten(self):
        (self.job / "acoustic-training.yaml").write_text("keep", encoding="utf-8")
        with self.assertRaises(FileExistsError):
            binarize.binarize_diffsinger(self.payload)
        self.assertEqual((self.job / "acoustic-training.yaml").read_text(encoding="utf-8"), "keep")


if __name__ == "__main__":
    unittest.main()
