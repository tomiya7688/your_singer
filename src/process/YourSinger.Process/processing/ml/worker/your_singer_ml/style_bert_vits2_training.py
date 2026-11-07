from __future__ import annotations

import json
import subprocess
from pathlib import Path

from .process_tree import classify, record_failure, run_reclaiming
from .run_report import build_run_report
from .style_bert_vits2_session import StyleBertVits2TrainingSession
from .style_bert_vits2_weighted_sampler import read_report


# {
#   責務: [train_style_bert_vits2: 公式の各前処理と学習をジョブ専用環境で実行する]
#   処理: [1: 入力と資源を検証する 2: 専用Pythonで各工程を実行する 3: 実在する成果物を返す]
#   エラー: [入力不一致・非ゼロ終了・空の学習データ・成果物不足で失敗する]
# }
def train_style_bert_vits2(payload: dict) -> dict:
    session = StyleBertVits2TrainingSession(payload)
    session.prepare()
    try:
        return _run_stages(payload, session)
    except BaseException as error:
        # 取消・タイムアウト・異常終了を区別して診断用に残す。result.json は書かず、成功として公開しない。
        record_failure(session.output, classify(error), str(error))
        raise


# { 責務: [_run_stages: 準備済みジョブの各工程を実行し、成果物と実行記録を返す(失敗は呼び出し側が記録する)] }
def _run_stages(payload: dict, session: StyleBertVits2TrainingSession) -> dict:
    log_path = session.output / "trainer.log"
    staged = None
    with log_path.open("x", encoding="utf-8") as log:
        for stage in (
            "initialize",
            "resample.py",
            "preprocess_text.py",
            "bert_gen.py",
            "style_gen.py",
            "train_ms.py",
        ):
            # 前処理CLIの戻り値を握りつぶすpreprocess_all.pyを経由しない。
            log.write(f"\n--- {stage} ---\n")
            log.flush()
            if stage == "train_ms.py":
                session.require_training_features()
            completed = run_reclaiming(
                session.command(stage),
                timeout=payload.get("stage_timeout_seconds"),
                cwd=session.output,
                env=session.environment(),
                check=False,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            if completed.returncode != 0:
                raise RuntimeError(
                    f"Style-Bert-VITS2の{stage}が終了コード{completed.returncode}で失敗しました。 log={log_path}"
                )
            if stage == "initialize":
                # 初期化方式が計画どおりに置かれたことを、学習の前に実ファイルで確認する。
                staged = session.stage_report()
    artifacts = session.require_artifacts()
    result = dict(
        exit_code=0,
        log_path=str(log_path),
        trainer_root=str(session.trainer),
        model_name=session.model_name,
        **artifacts,
    )
    if session.timbre_references:
        # 声質参照が指定されたのに追加できなかった場合は、学習成功として扱わず失敗にする。
        with log_path.open("a", encoding="utf-8") as log:
            log.write("\n--- timbre_style ---\n")
            log.flush()
            completed = run_reclaiming(
                session.command("timbre_style"),
                timeout=payload.get("stage_timeout_seconds"),
                cwd=session.output,
                env=session.environment(),
                check=False,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
        if completed.returncode != 0:
            raise RuntimeError(
                f"歌唱由来の声質スタイルの追加が終了コード{completed.returncode}で失敗しました。 log={log_path}"
            )
        result["timbre_style"] = json.loads(
            (session.output / "timbre_style_report.json").read_text(encoding="utf-8")
        )
    # 実際に行った学習の記録。指定(モード・seed)と成果物のhashを残す。初期重みの実読込は未観測(null)。
    artifacts = [
        ("config", Path(result["config_path"])),
        ("style_vectors", Path(result["style_vectors_path"])),
        *[("model_weights", Path(path)) for path in result["model_weights_paths"]],
    ]
    # 重みmanifestがあるジョブでは、重み付きサンプラーが学習中に実際に使われたことを要求する(使われなければ失敗)。
    weight_sampling = (
        read_report(session.output / "weight_sampling.json") if session.weight_manifest else None
    )
    result["run_report"] = build_run_report(
        payload.get("job_id", ""),
        payload.get("resolved_training_mode"),
        payload.get("seed"),
        0,
        session.output,
        artifacts,
        staged,
        "staged_before_launch" if staged is not None else None,
        weight_sampling,
    )
    # 結果の参照も非公開Voice Project内に残し、Exporterへ渡せるようにする。
    (session.output / "result.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return result
