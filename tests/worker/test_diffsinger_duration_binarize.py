import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from your_singer_ml import diffsinger_binarize as acoustic
from your_singer_ml import diffsinger_duration_binarize as duration
from your_singer_ml import diffsinger_weighted_sampler as sampler


# {
#   責務: [DiffSingerDurationBinarizeTests: Duration用の設定内容・起動引数・名前記録の照合・失敗の記録を確認する]
#   補足: [公式binarizationの本体は実行しない(代替の実行関数)。合成音声での実行はtools/smoke_diffsinger_binarize.pyと手動確認]
# }
class DiffSingerDurationBinarizeTests(unittest.TestCase):
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
        inputs = self.job / "duration-input"
        inputs.mkdir(parents=True)
        self.names = [f"segment_{i}" for i in range(12)]
        (inputs / "transcriptions.csv").write_text(
            "name,ph_seq,ph_dur,ph_num\n" + "".join(f"{n},k a,0.4 0.6,2\n" for n in self.names),
            encoding="utf-8",
        )
        (inputs / "dictionary-ja.txt").write_text("k\tk\na\ta\n", encoding="utf-8")
        self.payload = dict(
            trainer_root=str(self.bundle),
            project_root=str(self.project),
            job_id="job-1",
            speaker_id="spk",
        )

    def fake_run(self, record_names=True, code=0):
        def run(command, **kwargs):
            self.command = command
            if record_names:
                binary = self.job / "binary-duration"
                binary.mkdir(exist_ok=True)
                held = set(acoustic.choose_test_prefixes(self.names))
                train = [n for n in self.names if n not in held]
                (binary / sampler.NAMES_FILE).write_text(
                    json.dumps(dict(names=train)), encoding="utf-8"
                )
            return SimpleNamespace(returncode=code)

        return run

    def test_config_asks_for_duration_only_and_uses_the_variance_base(self):
        with patch(
            "your_singer_ml.diffsinger_duration_binarize.run_reclaiming",
            side_effect=self.fake_run(),
        ):
            result = duration.binarize_diffsinger_duration(self.payload)
        config = Path(result["config_path"]).read_text(encoding="utf-8")
        self.assertIn("variance.yaml", config)
        self.assertIn("predict_dur: true", config)
        self.assertIn("predict_pitch: false", config)
        self.assertIn("duration-input", config)
        self.assertTrue(self.command[1].endswith("diffsinger_binarize_entry.py"))
        self.assertNotIn("--exp_name", self.command)
        self.assertEqual(result["item_count"], 12 - len(result["test_prefixes"]))

    def test_missing_name_record_or_nonzero_exit_leaves_a_failure_marker(self):
        with patch(
            "your_singer_ml.diffsinger_duration_binarize.run_reclaiming",
            side_effect=self.fake_run(record_names=False),
        ):
            with self.assertRaises(ValueError):
                duration.binarize_diffsinger_duration(self.payload)
        self.assertEqual(
            json.loads((self.job / "run_failure.json").read_text(encoding="utf-8"))["kind"],
            "failed",
        )
        (self.job / duration.CONFIG_NAME).unlink()
        with patch(
            "your_singer_ml.diffsinger_duration_binarize.run_reclaiming",
            side_effect=self.fake_run(code=3),
        ):
            with self.assertRaisesRegex(RuntimeError, "終了コード3"):
                duration.binarize_diffsinger_duration(self.payload)

    def test_an_existing_config_or_missing_input_is_refused(self):
        (self.job / duration.CONFIG_NAME).write_text("keep", encoding="utf-8")
        with self.assertRaises(FileExistsError):
            duration.binarize_diffsinger_duration(self.payload)
        (self.job / duration.CONFIG_NAME).unlink()
        (self.job / "duration-input" / "dictionary-ja.txt").unlink()
        with self.assertRaises(FileNotFoundError):
            duration.binarize_diffsinger_duration(self.payload)


if __name__ == "__main__":
    unittest.main()
