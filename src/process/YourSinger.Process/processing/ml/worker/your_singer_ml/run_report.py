from __future__ import annotations

import hashlib
import json
from pathlib import Path

SCHEMA_VERSION = "1"


# {
#   責務: [sha256_file: ファイルのsha256を16進文字列で返す]
#   引数: [path: 対象ファイル]
#   戻り値: [hex: 小文字16進のsha256]
# }
def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


# {
#   責務: [build_run_report: worker が実際に行った学習の記録(run_manifest.json)を作って保存する]
#   処理: [1: 成果物を出力先からの相対パスとsha256にする 2: 要求された値と観測した値を分けて書く 3: output_dir/run_manifest.json に保存する]
#   引数: [job_id: ジョブID, resolved_mode: 要求された解決済みモード, seed: 要求されたseed, exit_code: Trainer終了コード,
#          output_dir: ジョブの出力先, artifacts: (kind, path) の一覧, initialization_observed: 実際に読み込んだ初期重み(未観測はNone)]
#   戻り値: [report: 保存した記録]
#   エラー: [成果物が出力先の外・欠損・空のとき ValueError/FileNotFoundError(成功を偽装しない)]
#   補足: [initialization_observed が None のときは「未観測」を意味し、指定どおりに初期化したとは主張しない]
# }
def build_run_report(
    job_id: str,
    resolved_mode: str | None,
    seed: int | None,
    exit_code: int,
    output_dir: Path,
    artifacts: list[tuple[str, Path]],
    initialization_observed: dict | None = None,
    initialization_evidence: str | None = None,
) -> dict:
    root = output_dir.resolve()
    entries = []
    for kind, path in artifacts:
        resolved = path.resolve()
        if root not in resolved.parents:
            raise ValueError(f"成果物が出力先の外にあります: {path}")
        if not resolved.is_file() or resolved.stat().st_size == 0:
            raise FileNotFoundError(f"成果物がありません、または空です: {path}")
        entries.append(
            dict(
                kind=kind, path=resolved.relative_to(root).as_posix(), sha256=sha256_file(resolved)
            )
        )
    report = dict(
        schema_version=SCHEMA_VERSION,
        job_id=job_id,
        resolved_mode=resolved_mode,
        seed=seed,
        trainer_exit_code=exit_code,
        initialization_observed=initialization_observed,
        # staged_before_launch: 起動前に置いた重みのhash確認。loaded: Trainerが読み込んだことの確認(現状は未実装)。
        initialization_evidence=initialization_evidence,
        artifacts=sorted(entries, key=lambda item: item["path"]),
    )
    (root / "run_manifest.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return report
