import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import call, mock_open, patch

from your_singer_ml import diffsinger_training


class DiffSingerTrainingTests(unittest.TestCase):
    def setUp(self):
        self.trainer_root = Path.cwd().resolve()
        self.trainer_python = self.trainer_root / "runtime" / "python.exe"
        self.entry = self.trainer_root / "source" / "scripts" / "train.py"
        self.config_path = Path(__file__).resolve()
        self.output_dir = self.trainer_root.parent / f"{self.trainer_root.name}-diffsinger-test-output"
        self.payload = {
            "trainer_root": str(self.trainer_root),
            "config_path": str(self.config_path),
            "exp_name": "test-job",
            "output_dir": str(self.output_dir),
        }

    def test_missing_trainer_entry_fails_before_starting_a_process(self):
        with patch.object(Path, "is_file", new=lambda path: False):
            with patch("your_singer_ml.diffsinger_training.subprocess.run") as run:
                with self.assertRaisesRegex(FileNotFoundError, "固定DiffSingerの学習入口"):
                    diffsinger_training.train_diffsinger(self.payload)
        run.assert_not_called()

    def test_missing_private_runtime_fails_before_starting_a_process(self):
        with patch.object(
            Path, "is_file", new=lambda path: path == self.entry
        ):
            with patch("your_singer_ml.diffsinger_training.subprocess.run") as run:
                with self.assertRaisesRegex(FileNotFoundError, "専用Python runtime"):
                    diffsinger_training.train_diffsinger(self.payload)
        run.assert_not_called()

    def test_output_inside_bundle_is_rejected_before_starting_a_process(self):
        payload = dict(self.payload, output_dir=str(self.trainer_root / "checkpoints"))
        with patch("your_singer_ml.diffsinger_training.subprocess.run") as run:
            with self.assertRaisesRegex(ValueError, "bundleの外"):
                diffsinger_training.train_diffsinger(payload)
        run.assert_not_called()

    def test_job_name_cannot_escape_the_per_job_checkpoint_directory(self):
        payload = dict(self.payload, exp_name="../other-job")
        with patch("your_singer_ml.diffsinger_training.subprocess.run") as run:
            with self.assertRaisesRegex(ValueError, "job名"):
                diffsinger_training.train_diffsinger(payload)
        run.assert_not_called()

    def test_success_uses_bundled_runtime_and_job_scoped_outputs(self):
        log_writer = mock_open()
        bundle_files = {self.entry, self.trainer_python, self.config_path}
        with patch.object(Path, "is_file", new=lambda path: path in bundle_files):
            with patch.object(Path, "mkdir") as mkdir:
                with patch.object(Path, "open", log_writer):
                    with patch(
                        "your_singer_ml.diffsinger_training.subprocess.run",
                        return_value=SimpleNamespace(returncode=0),
                    ) as run:
                        result = diffsinger_training.train_diffsinger(self.payload)

        command = run.call_args.args[0]
        self.assertEqual(command[0], str(self.trainer_python))
        self.assertEqual(command[1], str(self.entry))
        self.assertEqual(run.call_args.kwargs["cwd"], self.output_dir)
        self.assertEqual(run.call_args.kwargs["env"]["TEMP"], str(self.output_dir / "runtime-state" / "temp"))
        self.assertEqual(result["exit_code"], 0)
        self.assertEqual(result["log_path"], str(self.output_dir / "trainer.log"))
        self.assertIn(call(parents=True, exist_ok=True), mkdir.call_args_list)
        log_writer.assert_called_once_with("a", encoding="utf-8")

    def test_nonzero_exit_fails_and_keeps_the_log_path(self):
        log_writer = mock_open()
        bundle_files = {self.entry, self.trainer_python, self.config_path}
        with patch.object(Path, "is_file", new=lambda path: path in bundle_files):
            with patch.object(Path, "mkdir"):
                with patch.object(Path, "open", log_writer):
                    with patch(
                        "your_singer_ml.diffsinger_training.subprocess.run",
                        return_value=SimpleNamespace(returncode=7),
                    ):
                        with self.assertRaisesRegex(RuntimeError, "終了コード7") as error:
                            diffsinger_training.train_diffsinger(self.payload)

        self.assertIn(str(self.output_dir / "trainer.log"), str(error.exception))
        log_writer.assert_called_once_with("a", encoding="utf-8")


if __name__ == "__main__":
    unittest.main()

