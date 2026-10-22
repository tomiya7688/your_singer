from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path

ARTIFACTS = Path(__file__).resolve().parents[1] / "artifacts"
PYTHON_VERSION = "3.12.10"


# {
#   責務: [TrainerPythonRuntimeBuilder: 元Pythonを変更せず学習用の標準runtimeを構築する]
#   フィールド: [source_runtime: 入力Python, output_runtime: 検証後の新規配置先]
#   処理: [1: パスを検証する 2: 第三者依存を除いて複製する 3: pip初期化と検査後に配置する]
# }
class TrainerPythonRuntimeBuilder:
    # {
    #   責務: [__init__: 完全なPythonとartifacts内の独立した新規出力先を検証する]
    #   エラー: [venv・入力出力の重複・領域外・既存出力は拒否する]
    # }
    def __init__(self, source_runtime: Path, output_runtime: Path):
        self.source_runtime = source_runtime.resolve(strict=True)
        self.output_runtime = output_runtime.resolve()
        if output_runtime.exists() or output_runtime.is_symlink():
            raise FileExistsError("専用Pythonの配置先は未作成のフォルダにしてください。")
        if self.output_runtime == ARTIFACTS.resolve() or not self.output_runtime.is_relative_to(ARTIFACTS.resolve()):
            raise ValueError("専用Pythonの配置先はリポジトリのartifacts配下にしてください。")
        if (self.source_runtime.is_relative_to(self.output_runtime)
                or self.output_runtime.is_relative_to(self.source_runtime)):
            raise ValueError("入力Pythonと配置先は独立したフォルダにしてください。")
        if (self.source_runtime / "pyvenv.cfg").exists():
            raise ValueError("venvではなく完全なWindows Python runtimeを指定してください。")
        if not (self.source_runtime / "python.exe").is_file() or not (self.source_runtime / "Lib/ensurepip").is_dir():
            raise ValueError("python.exeとensurepipを含む完全なWindows Pythonが必要です。")

    # {
    #   責務: [_excluded_names: 第三者package・実行ラッパー・既存bytecodeを複製対象から除く]
    #   引数: [directory: コピー元の現在位置, names: 直下の名前]
    #   戻り値: [excluded: コピーしない名前]
    # }
    def _excluded_names(self, directory: str, names: list[str]) -> set[str]:
        excluded = {name for name in names if name == "__pycache__" or name.endswith(".pyc")}
        # site-packagesとScriptsだけを元の階層で除き、標準ライブラリは保持する。
        current_directory = Path(directory)
        if current_directory == self.source_runtime / "Lib":
            excluded.update(name for name in names if name.lower() == "site-packages")
        if current_directory == self.source_runtime:
            excluded.update(name for name in names if name.lower() == "scripts")
        return excluded

    # {
    #   責務: [_bootstrap: 複製先のensurepipを使いPython版・探索先・初期依存を検証する]
    #   引数: [runtime: 一時配置したPython]
    #   戻り値: [inventory: 専用Pythonの版と初期依存]
    #   エラー: [pip初期化失敗・版不一致・探索先不一致・余分な依存を拒否する]
    # }
    @staticmethod
    def _bootstrap(runtime: Path) -> dict:
        environment = os.environ.copy()
        for variable in ("PYTHONHOME", "PYTHONPATH"):
            environment.pop(variable, None)
        # ensurepipはPIP_*設定を初期化するため、既定cacheの親も一時領域へ分離する。
        scratch = runtime.parent / "scratch"
        folders = {"TEMP": "temp", "TMP": "temp", "LOCALAPPDATA": "local-app-data",
                   "APPDATA": "app-data", "HOME": "home", "USERPROFILE": "home"}
        for variable, folder in folders.items():
            directory = scratch / folder
            directory.mkdir(parents=True, exist_ok=True)
            environment[variable] = str(directory)
        python = runtime / "python.exe"
        subprocess.run([str(python), "-I", "-m", "ensurepip", "--upgrade", "--default-pip"],
                       check=True, env=environment)
        command = ("import importlib.metadata as m,json,platform,sys; "
                   "print(json.dumps(dict(python_version=platform.python_version(),prefix=sys.prefix,"
                   "packages={d.metadata['Name'].lower():d.version for d in m.distributions()})))")
        completed = subprocess.run([str(python), "-I", "-c", command],
                                   check=True, capture_output=True, text=True, env=environment)
        inventory = json.loads(completed.stdout)
        if inventory["python_version"] != PYTHON_VERSION or Path(inventory["prefix"]).resolve() != runtime.resolve():
            raise ValueError("専用Pythonの版または標準ライブラリの探索先が一致しません。")
        if set(inventory["packages"]) != {"pip"}:
            raise ValueError("専用Pythonへ固定前の第三者packageが混入しています。")
        return inventory

    # {
    #   責務: [build: 第三者依存を引き継がず検証済みの専用Pythonだけを新規配置する]
    #   エラー: [失敗時は未完成フォルダを公開せず、入力と既存出力を変更しない]
    # }
    def build(self) -> dict:
        self.output_runtime.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".trainer-python-", dir=self.output_runtime.parent) as temporary:
            runtime = Path(temporary) / "runtime"
            # 入力環境のpipx等を除外し、標準Pythonに同梱されたpipだけを再構成する。
            shutil.copytree(self.source_runtime, runtime, ignore=self._excluded_names)
            inventory = self._bootstrap(runtime)
            inventory.pop("prefix")  # 一時配置の絶対パスを完成bundleの探索先と誤認させない。
            inventory["source_packages_excluded"] = True
            (runtime / "python-bootstrap.json").write_text(json.dumps(inventory, indent=2), encoding="utf-8")
            if self.output_runtime.exists():
                raise FileExistsError("構築中に同名の専用Pythonが配置されました。")
            runtime.rename(self.output_runtime)
        return inventory


# { 責務: [main: Trainer builderから共通のPython複製処理を実行する] }
def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    inventory = TrainerPythonRuntimeBuilder(arguments.source, arguments.output).build()
    print(json.dumps(inventory))


if __name__ == "__main__":
    main()
