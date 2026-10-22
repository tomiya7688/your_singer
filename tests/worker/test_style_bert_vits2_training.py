import json
import os
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from your_singer_ml import style_bert_vits2_entry, style_bert_vits2_training
from your_singer_ml.style_bert_vits2_session import StyleBertVits2TrainingSession


# {
#   責務: [StyleBertVits2TrainingTests: Trainer接続の入力・工程順序・失敗境界を確認する]
#   処理: [1: 合成資源を作る 2: 起動と成果物境界を確認する]
# }
class StyleBertVits2TrainingTests(unittest.TestCase):
    # { 責務: [setUp: 学習品質の証拠に使わない合成入力と空の専用runtimeを用意する] }
    def setUp(self):
        # このテストは起動契約を扱う。固定bundleの整合性は専用テストで確認する。
        bundle_check = patch("your_singer_ml.style_bert_vits2_session.StyleBertVits2Bundle.validate")
        bundle_check.start()
        self.addCleanup(bundle_check.stop)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.trainer, self.dataset = root / "bundle", root / "job"
        required = ("runtime/python.exe", "resample.py", "preprocess_text.py", "bert_gen.py", "style_gen.py", "train_ms.py",
            "default_config.yml", "configs/config.json", "style_bert_vits2/constants.py",
            "pretrained/G_0.safetensors", "pretrained/D_0.safetensors", "pretrained/DUR_0.safetensors")
        for relative in required:
            path = self.trainer / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic-test-resource")
        (self.dataset / "raw").mkdir(parents=True)
        self.audio = self.dataset / "raw" / "sample.wav"
        self.audio.write_bytes(b"synthetic-test-audio")
        self.record = dict(job_id="job", dataset_fingerprint="fingerprint", auto_correction_enabled=False,
            items=[dict(audio_path=str(self.audio), speaker_name="話者", transcript="テスト")])
        self.save_record()
        (self.dataset / "esd.list").write_text("sample.wav|話者|JP|テスト\n", encoding="utf-8")
        self.payload = dict(trainer_root=str(self.trainer), dataset_dir=str(self.dataset), output_dir=str(self.dataset / "trainer"),
            model_name="話者モデル", job_id="job", dataset_fingerprint="fingerprint", auto_correction_enabled=False)

    # { 責務: [save_record: 合成入力の追跡情報を保存する] }
    def save_record(self):
        (self.dataset / "dataset.json").write_text(json.dumps(self.record, ensure_ascii=False), encoding="utf-8")

    # { 責務: [test_dataset_is_copied_and_original_is_preserved: 前処理が入力原本を書き換えないことを確認する] }
    def test_dataset_is_copied_and_original_is_preserved(self):
        session = StyleBertVits2TrainingSession(self.payload)
        session.prepare()
        copied = session.data / "raw" / self.audio.name
        self.assertEqual(copied.read_bytes(), self.audio.read_bytes())
        copied.write_bytes(b"changed")
        self.assertEqual(self.audio.read_bytes(), b"synthetic-test-audio")
        with self.assertRaises(FileExistsError):
            session.prepare()

    # { 責務: [test_mismatched_job_or_old_paths_are_rejected_before_output: 古い入力とジョブ不一致を拒否する] }
    def test_mismatched_job_or_old_paths_are_rejected_before_output(self):
        for field, value in (("job_id", "different"), ("dataset_fingerprint", "different"), ("auto_correction_enabled", True)):
            with self.subTest(field=field):
                with self.assertRaisesRegex(ValueError, "識別情報"):
                    StyleBertVits2TrainingSession(dict(self.payload, **{field: value})).prepare()
        (self.dataset / "esd.list").write_text("raw/sample.wav|話者|JP|テスト\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "esd.list"):
            StyleBertVits2TrainingSession(self.payload).prepare()
        self.assertFalse((self.dataset / "trainer").exists())

    # { 責務: [test_missing_runtime_never_starts_a_process: worker自身をPythonとして再実行しないことを確認する] }
    def test_missing_runtime_never_starts_a_process(self):
        (self.trainer / "runtime" / "python.exe").unlink()
        with patch.object(style_bert_vits2_training.subprocess, "run") as run:
            with self.assertRaisesRegex(FileNotFoundError, "専用Python"):
                style_bert_vits2_training.train_style_bert_vits2(self.payload)
        run.assert_not_called()

    # { 責務: [test_paths_and_reserved_model_names_are_rejected: 学習入力外の保存先と危険なモデル名を拒否する] }
    def test_paths_and_reserved_model_names_are_rejected(self):
        for model in ("../outside", "CON", "nul.json", "name.", "", "a" * 81):
            with self.subTest(model=model), self.assertRaises(ValueError):
                StyleBertVits2TrainingSession(dict(self.payload, model_name=model))
        with self.assertRaises(ValueError):
            StyleBertVits2TrainingSession(dict(self.payload, output_dir=str(self.dataset.parent / "outside")))

    # { 責務: [test_outside_audio_is_rejected: 追跡情報を書き換えて入力外の音声をコピーできないことを確認する] }
    def test_outside_audio_is_rejected(self):
        self.record["items"][0]["audio_path"] = str(self.trainer / "runtime" / "python.exe")
        self.save_record()
        with self.assertRaisesRegex(ValueError, "rawフォルダ"):
            StyleBertVits2TrainingSession(self.payload).prepare()

    # { 責務: [test_environment_and_commands_belong_to_the_job: 専用Python・設定・キャッシュ・入力先を照合する] }
    def test_environment_and_commands_belong_to_the_job(self):
        session = StyleBertVits2TrainingSession(self.payload)
        session.prepare()
        with patch.dict(os.environ, {"PYTHONHOME": "unrelated-python", "RANK": "9"}):
            environment = session.environment()
        self.assertNotIn("PYTHONHOME", environment)
        self.assertNotIn("RANK", environment)
        for name in ("TEMP", "TMP", "HOME", "APPDATA", "HF_HOME", "PYTHONPYCACHEPREFIX"):
            self.assertTrue(Path(environment[name]).is_relative_to(session.output))
        command = session.command("train_ms.py")
        self.assertEqual(command[0], str(session.python))
        self.assertEqual(command[command.index("--model") + 1], str(session.data))
        self.assertEqual(command[command.index("--config") + 1], str(session.data / "config.json"))

    # { 責務: [write_features: 公式形式を模した7列リストと非空の合成特徴を保存する] }
    def write_features(self, session):
        audio = session.data / "wavs" / self.audio.name
        audio.parent.mkdir(parents=True)
        for feature in (audio, audio.with_suffix(".bert.pt"), Path(str(audio) + ".npy")):
            feature.write_bytes(b"synthetic-test-feature")
        (session.data / "train.list").write_text(f"{audio}|話者|JP|テスト|t e|0 0|1 1\n", encoding="utf-8")
        (session.data / "val.list").write_text("", encoding="utf-8")

    # { 責務: [test_zero_exit_without_features_or_artifacts_is_not_success: ゼロ終了だけで学習成功としないことを確認する] }
    def test_zero_exit_without_features_or_artifacts_is_not_success(self):
        session = StyleBertVits2TrainingSession(self.payload)
        session.prepare()
        (session.data / "train.list").write_text("", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "空です"):
            session.require_training_features()
        self.write_features(session)
        session.require_training_features()
        with self.assertRaisesRegex(FileNotFoundError, "推論成果物"):
            session.require_artifacts()
        (session.data / "wavs" / "sample.bert.pt").unlink()
        with self.assertRaisesRegex(FileNotFoundError, "特徴"):
            session.require_training_features()

    # { 責務: [test_failed_preprocessing_stops_before_training_and_keeps_log: 工程失敗時に学習を開始しないことを確認する] }
    def test_failed_preprocessing_stops_before_training_and_keeps_log(self):
        with patch.object(style_bert_vits2_training.subprocess, "run", side_effect=[SimpleNamespace(returncode=0), SimpleNamespace(returncode=7)]) as run:
            with self.assertRaisesRegex(RuntimeError, "resample.py.*終了コード7"):
                style_bert_vits2_training.train_style_bert_vits2(self.payload)
        self.assertEqual(run.call_count, 2)
        self.assertTrue((self.dataset / "trainer" / "trainer.log").is_file())

    # { 責務: [test_all_stages_return_artifact_references: 全工程後にログと成果物の参照が返ることを確認する] }
    def test_all_stages_return_artifact_references(self):
        stages = []

        # { 責務: [complete_stage: 実学習を行わず工程間のファイル契約を模す] }
        def complete_stage(command, **kwargs):
            stage = command[4]
            stages.append(stage)
            session = StyleBertVits2TrainingSession(self.payload)
            if stage == "style_gen.py":
                self.write_features(session)
            if stage == "train_ms.py":
                session.assets.mkdir(parents=True)
                (session.assets / "config.json").write_text(json.dumps(dict(version="2.7.0", model_name=session.model_name)), encoding="utf-8")
                for name in ("style_vectors.npy", "話者モデル_e1_s1.safetensors"):
                    (session.assets / name).write_bytes(b"synthetic-test-artifact")
            return SimpleNamespace(returncode=0)

        with patch.object(style_bert_vits2_training.subprocess, "run", side_effect=complete_stage):
            result = style_bert_vits2_training.train_style_bert_vits2(self.payload)
        self.assertEqual(stages, ["initialize", "resample.py", "preprocess_text.py", "bert_gen.py", "style_gen.py", "train_ms.py"])
        self.assertEqual(result["exit_code"], 0)
        self.assertTrue(Path(result["style_vectors_path"]).is_file())
        self.assertTrue((self.dataset / "trainer" / "result.json").is_file())

    # { 責務: [test_initialization_uses_official_configuration_in_the_job: 公式設定の継承と元資源の保護を確認する] }
    def test_initialization_uses_official_configuration_in_the_job(self):
        session = StyleBertVits2TrainingSession(self.payload)
        session.prepare()
        settings = dict(train_ms=dict(env={"RANK": 0}), bert_gen=dict(device="cuda"))
        hyperparameters = dict(version="2.7.0", data={}, train={}, model={})
        (self.trainer / "default_config.yml").write_text(json.dumps(settings), encoding="utf-8")
        config_source = self.trainer / "configs" / "config.json"
        config_source.write_text(json.dumps(hyperparameters), encoding="utf-8")
        fake_yaml = SimpleNamespace(safe_load=json.loads, safe_dump=lambda content, **kwargs: json.dumps(content))
        with patch.dict("sys.modules", {"yaml": fake_yaml}):
            with patch.object(style_bert_vits2_entry, "require_training_runtime"):
                style_bert_vits2_entry.initialize(self.trainer, session.output)
        generated = json.loads((session.data / "config.json").read_text(encoding="utf-8"))
        self.assertEqual(generated["model_name"], "話者モデル")
        self.assertEqual(generated["data"]["training_files"], str(session.data / "train.list"))
        self.assertEqual(json.loads(config_source.read_text(encoding="utf-8")), hyperparameters)
        self.assertFalse((self.trainer / "config.yml").exists())

    # { 責務: [test_incompatible_training_runtimes_fail_before_data_preprocessing: CPU環境と辞書非対応APIを拒否する] }
    def test_incompatible_training_runtimes_fail_before_data_preprocessing(self):
        fake_torch = SimpleNamespace(cuda=SimpleNamespace(is_available=lambda: False))
        with patch.dict("sys.modules", {"torch": fake_torch}):
            with self.assertRaisesRegex(RuntimeError, "CUDA"):
                style_bert_vits2_entry.require_training_runtime()
        fake_torch.cuda.is_available = lambda: True
        with patch.dict("sys.modules", {"torch": fake_torch, "pyopenjtalk": SimpleNamespace()}):
            with self.assertRaisesRegex(RuntimeError, "pyopenjtalk-dict"):
                style_bert_vits2_entry.require_training_runtime()


if __name__ == "__main__":
    unittest.main()
