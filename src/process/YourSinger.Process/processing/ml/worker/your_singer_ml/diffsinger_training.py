from __future__ import annotations

import os
import re
import subprocess
from pathlib import Path


# {
#   責務: [_resolve_training_paths: 同梱bundleとジョブ別出力先を検証する]
#   処理: [1: 固定ソースとruntimeの入口を照合する 2: 出力先がbundle外であることを確認する]
#   引数: [payload: trainer、config、job名、出力先を含む要求]
#   戻り値: [paths: trainer入口、config、job名、出力先]
#   エラー: [必須ファイルがない、job名が不正、出力先がbundle内の場合に失敗する]
# }
def _resolve_training_paths(payload: dict) -> tuple[Path, Path, Path, Path, str, Path]:
    trainer_root = Path(str(payload["trainer_root"])).resolve(strict=True)
    trainer_python = trainer_root / "runtime" / "python.exe"
    trainer_entry = trainer_root / "source" / "scripts" / "train.py"
    config_path = Path(str(payload["config_path"])).resolve(strict=True)
    exp_name = str(payload["exp_name"])
    output_dir = Path(str(payload["output_dir"])).resolve()

    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", exp_name):
        raise ValueError("DiffSinger job名にパス区切りまたは使用できない文字があります。")
    if trainer_root in output_dir.parents or trainer_root == output_dir:
        raise ValueError("DiffSinger学習出力先は同梱bundleの外に指定してください。")
    if not trainer_entry.is_file():
        raise FileNotFoundError(f"固定DiffSingerの学習入口がありません: {trainer_entry}")
    if not trainer_python.is_file():
        raise FileNotFoundError(f"DiffSinger専用Python runtimeがありません: {trainer_python}")
    if not config_path.is_file():
        raise FileNotFoundError(f"学習configがありません: {config_path}")
    return trainer_root, trainer_python, trainer_entry, config_path, exp_name, output_dir


# {
#   責務: [_build_training_environment: Pythonと音声処理の一時領域をジョブ内へ固定する]
#   処理: [1: ジョブ別runtime-stateを作る 2: 各ライブラリのcache環境変数を返す]
#   引数: [output_dir: ジョブ専用のtrainer出力先]
#   戻り値: [environment: subprocessへ渡す環境変数]
# }
def _build_training_environment(output_dir: Path) -> dict[str, str]:
    runtime_state = output_dir / "runtime-state"
    environment = os.environ.copy()
    state_directories = {
        "TEMP": runtime_state / "temp",
        "TMP": runtime_state / "temp",
        "HOME": runtime_state / "home",
        "USERPROFILE": runtime_state / "home",
        "APPDATA": runtime_state / "app-data",
        "LOCALAPPDATA": runtime_state / "local-app-data",
        "MPLCONFIGDIR": runtime_state / "matplotlib",
        "NUMBA_CACHE_DIR": runtime_state / "numba",
        "TORCH_HOME": runtime_state / "torch",
        "PYTHONPYCACHEPREFIX": runtime_state / "bytecode",
    }
    # 各キャッシュを配布bundleから分離し、書込権限のあるジョブ領域に置く。
    for variable, directory in state_directories.items():
        directory.mkdir(parents=True, exist_ok=True)
        environment[variable] = str(directory)
    environment["WANDB_MODE"] = "disabled"
    return environment


# {
#   責務: [train_diffsinger: 固定DiffSinger acoustic Trainerを専用runtimeで実行する]
#   処理: [1: bundleと出力先を検証する 2: 公式Trainerを実行しログを追記する]
#   引数: [payload: trainer、runtime、config、job名、出力先を含む要求]
#   戻り値: [result: 終了コード、ログ、実行設定]
#   エラー: [bundleが不正、Trainer起動失敗、または非0終了時に失敗する]
# }
def train_diffsinger(payload: dict) -> dict:
    trainer_root, trainer_python, trainer_entry, config_path, exp_name, output_dir = (
        _resolve_training_paths(payload)
    )
    output_dir.mkdir(parents=True, exist_ok=True)
    environment = _build_training_environment(output_dir)
    command = [
        str(trainer_python),
        str(trainer_entry),
        "--config",
        str(config_path),
        "--exp_name",
        exp_name,
        "--reset",
    ]

    log_path = output_dir / "trainer.log"
    # 同じjobを再実行しても以前のログを上書きしない。
    with log_path.open("a", encoding="utf-8") as log_file:
        completed = subprocess.run(
            command,
            cwd=output_dir,
            check=False,
            text=True,
            encoding="utf-8",
            errors="replace",
            stdout=log_file,
            stderr=subprocess.STDOUT,
            env=environment,
        )

    if completed.returncode != 0:
        raise RuntimeError(
            f"DiffSinger trainerが終了コード{completed.returncode}で失敗しました。"
            f" log={log_path}"
        )

    return {
        "exit_code": completed.returncode,
        "log_path": str(log_path),
        "trainer_root": str(trainer_root),
        "config_path": str(config_path),
        "exp_name": exp_name,
    }
