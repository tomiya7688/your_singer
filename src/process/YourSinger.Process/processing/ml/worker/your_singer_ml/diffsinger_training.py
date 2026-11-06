from __future__ import annotations

import json
import os
import re
import subprocess
from pathlib import Path

from .diffsinger_initialization import (
    collect_checkpoints,
    prepare_initialization,
    verify_dumped_config,
)
from .diffsinger_weighted_sampler import read_report
from .process_tree import classify, record_failure, run_reclaiming
from .run_report import build_run_report


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


# { 責務: [_has_weight_manifest: 学習入力が区間Weight manifestを持つか(重み付き学習が必要か)を返す] }
def _has_weight_manifest(job_dir: Path) -> bool:
    dataset = job_dir / "dataset.json"
    if not dataset.is_file():
        return False
    try:
        return (
            json.loads(dataset.read_text(encoding="utf-8-sig")).get("weight_manifest_file")
            is not None
        )
    except (OSError, ValueError):
        return False


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
    try:
        return _run_training(
            payload, trainer_root, trainer_python, trainer_entry, config_path, exp_name, output_dir
        )
    except BaseException as error:
        # 取消・タイムアウト・異常終了を区別して診断用に残す。成功として公開しない。
        record_failure(output_dir, classify(error), str(error))
        raise


# { 責務: [_run_training: 検証済みのパスで学習を実行し、成果物と実行記録を返す(失敗は呼び出し側が記録する)] }
def _run_training(
    payload, trainer_root, trainer_python, trainer_entry, config_path, exp_name, output_dir
) -> dict:
    # 学習モード(fine_tune / from_scratch)が指定されたときは、初期化方式を固定するoverlayのconfigで起動する。
    plan = prepare_initialization(payload, config_path, output_dir, exp_name)
    environment = _build_training_environment(output_dir)
    # 重み付きサンプラーの使用記録は出力先へ書く(音響とDurationの学習で出力先が異なるため、入口へ明示する)。
    environment["YOURSINGER_WEIGHT_REPORT"] = str(output_dir / "weight_sampling.json")
    train_arguments = [
        "--config",
        str(plan.overlay_path if plan else config_path),
        "--exp_name",
        exp_name,
        "--reset",
    ]
    # 重みmanifestを持つ学習入力(job直下のdataset.json)では、重み付きサンプラーへ差し替える入口で起動する。
    job_dir = output_dir.parent
    weighted = _has_weight_manifest(job_dir)
    if weighted:
        command = [
            str(trainer_python),
            str(Path(__file__).with_name("diffsinger_train_entry.py")),
            str(trainer_root),
            str(job_dir),
            str(int(payload.get("seed") or 0)),
            "--",
            *train_arguments,
        ]
    else:
        command = [str(trainer_python), str(trainer_entry), *train_arguments]

    log_path = output_dir / "trainer.log"
    # 同じjobを再実行しても以前のログを上書きしない。
    with log_path.open("a", encoding="utf-8") as log_file:
        completed = run_reclaiming(
            command,
            timeout=payload.get("stage_timeout_seconds"),
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
            f"DiffSinger trainerが終了コード{completed.returncode}で失敗しました。 log={log_path}"
        )

    result = {
        "exit_code": completed.returncode,
        "log_path": str(log_path),
        "trainer_root": str(trainer_root),
        "config_path": str(config_path),
        "exp_name": exp_name,
    }
    # 重み付きサンプラーが学習中に実際に使われたことを要求する(使われなければ失敗)。
    weight_sampling = read_report(output_dir / "weight_sampling.json") if weighted else None
    if weight_sampling is not None:
        result["weight_sampling"] = weight_sampling
    if plan is not None:
        # Trainerが実際に使った設定と、出力されたcheckpointを計画と照合して記録する。
        verify_dumped_config(plan, output_dir, exp_name)
        checkpoints = collect_checkpoints(output_dir, exp_name)
        result["run_report"] = build_run_report(
            str(payload.get("job_id", exp_name)),
            plan.mode,
            payload.get("seed"),
            completed.returncode,
            output_dir,
            [("checkpoint", path) for path in checkpoints],
            plan.staged,
            "staged_before_launch",
            weight_sampling,
        )
    return result
