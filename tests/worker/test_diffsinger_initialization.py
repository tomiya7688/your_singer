import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from your_singer_ml import diffsinger_initialization as init
from your_singer_ml import diffsinger_training


# {
#   責務: [DiffSingerInitializationTests: 学習モードと初期重みの契約(overlay・起動前の照合・Trainer設定の確認)を検証する]
#   補足: [固定ソース(utils/hparams.py・basics/base_task.py)の挙動に合わせた合成確認。実GPU・実際の重みの読込は未検証]
# }
class DiffSingerInitializationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.config = self.root / "acoustic.yaml"
        self.config.write_text("base_config: []\n", encoding="utf-8")
        self.output = self.root / "job" / "trainer"
        self.weights = self.root / "pretrained" / "acoustic.ckpt"
        self.weights.parent.mkdir()
        self.weights.write_bytes(b"acoustic-weights")
        self.sha = hashlib.sha256(b"acoustic-weights").hexdigest()

    def fine_tune(self, **overrides):
        component = dict(name="acoustic", sha256=self.sha, path=str(self.weights))
        return dict(
            resolved_training_mode="fine_tune",
            initialization=dict(type="pretrained_checkpoint", components=[component]),
            **overrides,
        )

    def scratch(self):
        return dict(
            resolved_training_mode="from_scratch",
            initialization=dict(type="random_initialization", components=[]),
        )

    def prepare(self, payload):
        return init.prepare_initialization(payload, self.config, self.output, "job-1")

    def test_legacy_payload_without_a_mode_is_unchanged(self):
        self.assertIsNone(self.prepare({}))
        self.assertFalse(self.output.exists())

    def test_fine_tune_overlay_enables_finetune_with_the_verified_checkpoint(self):
        plan = self.prepare(self.fine_tune())
        text = plan.overlay_path.read_text(encoding="utf-8")
        self.assertIn("finetune_enabled: true", text)
        self.assertIn("finetune_strict_shapes: true", text)
        self.assertIn(json.dumps(str(self.weights), ensure_ascii=True), text)
        self.assertIn(json.dumps(str(self.config), ensure_ascii=True), text)
        self.assertEqual(plan.staged["components"][0]["sha256"], self.sha)

    def test_from_scratch_overlay_disables_finetune_and_has_no_components(self):
        plan = self.prepare(self.scratch())
        text = plan.overlay_path.read_text(encoding="utf-8")
        self.assertIn("finetune_enabled: false", text)
        self.assertIn("finetune_ckpt_path: null", text)
        self.assertEqual(plan.staged, dict(type="random_initialization", components=[]))

    def test_inconsistent_modes_and_checkpoints_are_rejected_before_any_output(self):
        bad_hash = self.fine_tune()
        bad_hash["initialization"]["components"][0]["sha256"] = "0" * 64
        with self.assertRaises(ValueError):
            self.prepare(bad_hash)
        missing = self.fine_tune()
        missing["initialization"]["components"][0]["path"] = str(self.root / "none.ckpt")
        with self.assertRaises(FileNotFoundError):
            self.prepare(missing)
        scratch_with_weights = self.scratch()
        scratch_with_weights["initialization"]["components"] = [
            dict(name="acoustic", sha256=self.sha, path=str(self.weights))
        ]
        with self.assertRaises(ValueError):
            self.prepare(scratch_with_weights)
        with self.assertRaises(ValueError):
            self.prepare(dict(resolved_training_mode="auto"))
        with self.assertRaises(ValueError):
            self.prepare(
                dict(
                    resolved_training_mode="fine_tune",
                    initialization=dict(type="pretrained_checkpoint", components=[]),
                )
            )
        self.assertFalse((self.output / "init-overlay.yaml").exists())

    def test_existing_checkpoint_in_the_work_dir_is_rejected_because_the_trainer_would_resume(self):
        work = self.output / "checkpoints" / "job-1"
        work.mkdir(parents=True)
        (work / "model_ckpt_steps_10.ckpt").write_bytes(b"x")
        with self.assertRaises(ValueError):
            self.prepare(self.scratch())

    def test_existing_overlay_is_never_overwritten(self):
        self.prepare(self.scratch())
        with self.assertRaises(FileExistsError):
            self.prepare(self.scratch())

    def dump_config(self, text):
        work = self.output / "checkpoints" / "job-1"
        work.mkdir(parents=True, exist_ok=True)
        (work / "config.yaml").write_text(text, encoding="utf-8")

    def test_dumped_config_must_match_the_plan(self):
        plan = self.prepare(self.fine_tune())
        with self.assertRaises(RuntimeError):
            init.verify_dumped_config(plan, self.output, "job-1")
        self.dump_config("finetune_enabled: false\nfinetune_ckpt_path: null\n")
        with self.assertRaises(RuntimeError):
            init.verify_dumped_config(plan, self.output, "job-1")
        self.dump_config(f"finetune_enabled: true\nfinetune_ckpt_path: {self.weights}\n")
        init.verify_dumped_config(plan, self.output, "job-1")
        self.dump_config("finetune_enabled: true\nfinetune_ckpt_path: other.ckpt\n")
        with self.assertRaises(RuntimeError):
            init.verify_dumped_config(plan, self.output, "job-1")

    def test_from_scratch_dumped_config_must_not_enable_finetune(self):
        plan = self.prepare(self.scratch())
        self.dump_config("finetune_enabled: false\nfinetune_ckpt_path: null\n")
        init.verify_dumped_config(plan, self.output, "job-1")
        self.dump_config("finetune_enabled: false\nfinetune_ckpt_path: base.ckpt\n")
        with self.assertRaises(RuntimeError):
            init.verify_dumped_config(plan, self.output, "job-1")

    def test_collect_checkpoints_requires_nonempty_outputs(self):
        with self.assertRaises(FileNotFoundError):
            init.collect_checkpoints(self.output, "job-1")
        work = self.output / "checkpoints" / "job-1"
        work.mkdir(parents=True)
        (work / "model_ckpt_steps_5.ckpt").write_bytes(b"")
        with self.assertRaises(FileNotFoundError):
            init.collect_checkpoints(self.output, "job-1")
        (work / "model_ckpt_steps_5.ckpt").write_bytes(b"x")
        self.assertEqual(
            [p.name for p in init.collect_checkpoints(self.output, "job-1")],
            ["model_ckpt_steps_5.ckpt"],
        )

    def test_train_diffsinger_runs_with_the_overlay_and_records_a_run_report(self):
        trainer = self.root / "bundle"
        (trainer / "runtime").mkdir(parents=True)
        (trainer / "runtime" / "python.exe").write_bytes(b"py")
        (trainer / "source" / "scripts").mkdir(parents=True)
        (trainer / "source" / "scripts" / "train.py").write_bytes(b"entry")
        payload = dict(
            self.fine_tune(seed=3, job_id="job-1"),
            trainer_root=str(trainer),
            config_path=str(self.config),
            exp_name="job-1",
            output_dir=str(self.output),
        )

        def fake_run(command, **kwargs):
            # 固定Trainerの代わりに、保存configとcheckpointを作る。
            self.assertEqual(
                command[command.index("--config") + 1], str(self.output / "init-overlay.yaml")
            )
            self.dump_config(f"finetune_enabled: true\nfinetune_ckpt_path: {self.weights}\n")
            (self.output / "checkpoints" / "job-1" / "model_ckpt_steps_1.ckpt").write_bytes(
                b"trained"
            )
            return SimpleNamespace(returncode=0)

        with patch("your_singer_ml.diffsinger_training.subprocess.run", side_effect=fake_run):
            result = diffsinger_training.train_diffsinger(payload)
        report = result["run_report"]
        self.assertEqual(report["resolved_mode"], "fine_tune")
        self.assertEqual(report["seed"], 3)
        self.assertEqual(report["initialization_evidence"], "staged_before_launch")
        self.assertEqual(
            report["artifacts"][0]["path"], "checkpoints/job-1/model_ckpt_steps_1.ckpt"
        )
        self.assertTrue((self.output / "run_manifest.json").is_file())

    def test_zero_exit_without_a_checkpoint_is_not_success(self):
        trainer = self.root / "bundle"
        (trainer / "runtime").mkdir(parents=True)
        (trainer / "runtime" / "python.exe").write_bytes(b"py")
        (trainer / "source" / "scripts").mkdir(parents=True)
        (trainer / "source" / "scripts" / "train.py").write_bytes(b"entry")
        payload = dict(
            self.scratch(),
            trainer_root=str(trainer),
            config_path=str(self.config),
            exp_name="job-1",
            output_dir=str(self.output),
        )

        def fake_run(command, **kwargs):
            self.dump_config("finetune_enabled: false\nfinetune_ckpt_path: null\n")
            return SimpleNamespace(returncode=0)

        with patch("your_singer_ml.diffsinger_training.subprocess.run", side_effect=fake_run):
            with self.assertRaises(FileNotFoundError):
                diffsinger_training.train_diffsinger(payload)


if __name__ == "__main__":
    unittest.main()
