from __future__ import annotations

import subprocess
import sys
from pathlib import Path


def train_style_bert_vits2(payload: dict) -> dict:
    trainer_root = Path(str(payload["trainer_root"]))
    model_name = str(payload["model_name"])
    output_dir = Path(str(payload["output_dir"]))

    preprocess = trainer_root / "preprocess_all.py"
    train = trainer_root / "train_ms.py"

    if not preprocess.is_file():
        raise FileNotFoundError(
            f"Style-Bert-VITS2 preprocess_all.pyが同梱されていません: {preprocess}"
        )
    if not train.is_file():
        raise FileNotFoundError(
            f"Style-Bert-VITS2 train_ms.pyが同梱されていません: {train}"
        )

    output_dir.mkdir(parents=True, exist_ok=True)
    log_path = output_dir / "trainer.log"

    commands = [
        [sys.executable, str(preprocess), "-m", model_name],
        [sys.executable, str(train), "-m", model_name],
    ]

    with log_path.open("w", encoding="utf-8") as log:
        for command in commands:
            completed = subprocess.run(
                command,
                cwd=trainer_root,
                check=False,
                text=True,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            if completed.returncode != 0:
                raise RuntimeError(
                    f"Style-Bert-VITS2処理が終了コード{completed.returncode}で失敗しました。"
                    f" log={log_path}"
                )

    return {
        "exit_code": 0,
        "log_path": str(log_path),
        "trainer_root": str(trainer_root),
        "model_name": model_name,
    }
