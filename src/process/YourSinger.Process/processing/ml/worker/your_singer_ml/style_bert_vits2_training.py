from __future__ import annotations

import json
import subprocess

from .style_bert_vits2_session import StyleBertVits2TrainingSession


# {
#   責務: [train_style_bert_vits2: 公式の各前処理と学習をジョブ専用環境で実行する]
#   処理: [1: 入力と資源を検証する 2: 専用Pythonで各工程を実行する 3: 実在する成果物を返す]
#   エラー: [入力不一致・非ゼロ終了・空の学習データ・成果物不足で失敗する]
# }
def train_style_bert_vits2(payload: dict) -> dict:
    session = StyleBertVits2TrainingSession(payload)
    session.prepare()
    log_path = session.output / "trainer.log"
    with log_path.open("x", encoding="utf-8") as log:
        for stage in ("initialize", "resample.py", "preprocess_text.py", "bert_gen.py", "style_gen.py", "train_ms.py"):
            # 前処理CLIの戻り値を握りつぶすpreprocess_all.pyを経由しない。
            log.write(f"\n--- {stage} ---\n")
            log.flush()
            if stage == "train_ms.py":
                session.require_training_features()
            completed = subprocess.run(
                session.command(stage), cwd=session.output, env=session.environment(),
                check=False, stdout=log, stderr=subprocess.STDOUT,
            )
            if completed.returncode != 0:
                raise RuntimeError(
                    f"Style-Bert-VITS2の{stage}が終了コード{completed.returncode}で失敗しました。 log={log_path}"
                )
    artifacts = session.require_artifacts()
    result = dict(exit_code=0, log_path=str(log_path), trainer_root=str(session.trainer),
                  model_name=session.model_name, **artifacts)
    # 結果の参照も非公開Voice Project内に残し、Exporterへ渡せるようにする。
    (session.output / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return result
