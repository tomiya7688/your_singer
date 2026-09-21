from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path


def train_diffsinger(payload: dict) -> dict:
    trainer_root = Path(str(payload["trainer_root"]))
    config_path = Path(str(payload["config_path"]))
    exp_name = str(payload["exp_name"])
    output_dir = Path(str(payload["output_dir"]))

    entry = trainer_root / "tasks" / "run.py"
    if not entry.is_file():
        raise FileNotFoundError(
            f"DiffSinger trainerが同梱されていません: {entry}"
        )
    if not config_path.is_file():
        raise FileNotFoundError(f"学習configがありません: {config_path}")

    output_dir.mkdir(parents=True, exist_ok=True)
    command = [
        sys.executable,
        str(entry),
        "--config",
        str(config_path),
        "--exp_name",
        exp_name,
        "--reset",
    ]

    completed = subprocess.run(
        command,
        cwd=trainer_root,
        check=False,
        text=True,
        capture_output=True,
    )

    log_path = output_dir / "trainer.log"
    log_path.write_text(
        completed.stdout + "\n--- stderr ---\n" + completed.stderr,
        encoding="utf-8",
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
