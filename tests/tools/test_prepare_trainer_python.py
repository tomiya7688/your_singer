from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

REPOSITORY = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPOSITORY / "tools"))
from prepare_trainer_python import TrainerPythonRuntimeBuilder


# {
#   責務: [TrainerPythonRuntimeTests: runtime構築の混入防止と失敗時の保存状態を確認する]
#   フィールド: [temporary: artifacts内の検証領域, source: 入力runtime, output: 新規配置先]
# }
class TrainerPythonRuntimeTests(unittest.TestCase):
    # { 責務: [setUp: 配布対象と混ざらない検証領域を作る] }
    def setUp(self):
        root = REPOSITORY / "artifacts/trainer-python-tests"
        root.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=root)
        self.addCleanup(self.temporary.cleanup)
        self.source = Path(self.temporary.name) / "source"
        self.output = Path(self.temporary.name) / "output"
        (self.source / "Lib/ensurepip").mkdir(parents=True)
        (self.source / "python.exe").write_bytes(b"fixture")

    # { 責務: [test_existing_output_is_preserved: 配置済みruntimeを上書きしない] }
    def test_existing_output_is_preserved(self):
        self.output.mkdir()
        marker = self.output / "keep.txt"
        marker.write_text("unchanged")
        with self.assertRaises(FileExistsError):
            TrainerPythonRuntimeBuilder(self.source, self.output)
        self.assertEqual("unchanged", marker.read_text())

    # { 責務: [test_unsafe_runtime_paths_are_rejected: venv・入力との重複・領域外を拒否する] }
    def test_unsafe_runtime_paths_are_rejected(self):
        for output in (self.source / "nested", REPOSITORY / "outside-artifacts"):
            with self.subTest(output=output), self.assertRaises(ValueError):
                TrainerPythonRuntimeBuilder(self.source, output)
        (self.source / "pyvenv.cfg").write_text("fixture")
        with self.assertRaises(ValueError):
            TrainerPythonRuntimeBuilder(self.source, self.output)

    # { 責務: [test_bootstrap_failure_does_not_publish_output: 失敗時に入力と既存配置を保護する] }
    def test_bootstrap_failure_does_not_publish_output(self):
        builder = TrainerPythonRuntimeBuilder(self.source, self.output)
        with patch.object(builder, "_bootstrap", side_effect=RuntimeError("bootstrap failed")):
            with self.assertRaisesRegex(RuntimeError, "bootstrap failed"):
                builder.build()
        self.assertFalse(self.output.exists())
        self.assertEqual(b"fixture", (self.source / "python.exe").read_bytes())
        self.assertEqual([self.source], list(self.source.parent.iterdir()))

    # {
    #   責務: [test_real_python_excludes_source_packages: 追加依存つきの実Pythonを標準runtimeへ複製する]
    #   処理: [1: 入力複製に混入用packageを置く 2: 本物のensurepipを実行する 3: 出力と元入力を照合する]
    # }
    @unittest.skipUnless(os.name == "nt" and os.environ.get("YOURSINGER_TRAINER_PYTHON_SOURCE"),
                         "完全なWindows Python3.12.10の入力先を指定した場合だけ実行する")
    def test_real_python_excludes_source_packages(self):
        original = Path(os.environ["YOURSINGER_TRAINER_PYTHON_SOURCE"])
        shutil.copytree(original, self.source, dirs_exist_ok=True,
                        ignore=shutil.ignore_patterns("site-packages", "Scripts", "__pycache__", "*.pyc"))
        # 元の開発環境を変更せず、CI runnerのpipx混入と同じ配置を再現する。
        packages = self.source / "Lib/site-packages"
        metadata = packages / "unwanted_runtime_package-1.0.dist-info"
        metadata.mkdir(parents=True)
        (metadata / "METADATA").write_text("Name: unwanted-runtime-package\nVersion: 1.0\n")
        module = packages / "unwanted_runtime_package.py"
        module.write_text("raise RuntimeError('must not load in Trainer')")
        scripts = self.source / "Scripts"
        scripts.mkdir()
        (scripts / "unwanted-tool.exe").write_bytes(b"keep-source")
        inventory = TrainerPythonRuntimeBuilder(self.source, self.output).build()
        command = ("import importlib.util,json; "
                   "print(json.dumps(dict(unwanted=importlib.util.find_spec('unwanted_runtime_package') is not None)))")
        completed = subprocess.run([str(self.output / "python.exe"), "-I", "-c", command],
                                   check=True, capture_output=True, text=True)
        self.assertFalse(json.loads(completed.stdout)["unwanted"])
        self.assertEqual({"pip"}, set(inventory["packages"]))
        self.assertFalse((self.output / "Scripts/unwanted-tool.exe").exists())
        self.assertTrue(module.is_file())
        self.assertEqual(b"keep-source", (scripts / "unwanted-tool.exe").read_bytes())


if __name__ == "__main__":
    unittest.main()
