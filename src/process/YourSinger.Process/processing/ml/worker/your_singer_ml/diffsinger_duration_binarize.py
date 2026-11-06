from __future__ import annotations

import re
from pathlib import Path

from .diffsinger_binarize import ENTRY_NAME, choose_test_prefixes, read_clip_names
from .diffsinger_initialization import _yaml_text
from .diffsinger_weighted_sampler import read_item_names
from .process_tree import classify, record_failure, run_reclaiming

CONFIG_NAME = "duration-training.yaml"


# {
#   責務: [write_duration_config: Duration入力から、公式variance binarizationが読む設定(overlay)を作る]
#   処理: [固定ソースの configs/variance.yaml を base_config に取り、辞書・データセット・検証分割・binary_data_dirと、Durationだけを学習する指定を書く]
#   戻り値: [config_path, binary_dir, test_prefixes]
#   エラー: [設定が既にある(上書きしない)、入力のCSV/辞書が無いときに失敗する]
# }
def write_duration_config(
    job_dir: Path, source: Path, speaker_id: str
) -> tuple[Path, Path, list[str]]:
    duration = job_dir / "duration-input"
    csv_path, dictionary = duration / "transcriptions.csv", duration / "dictionary-ja.txt"
    if not csv_path.is_file() or not dictionary.is_file():
        raise FileNotFoundError("Duration入力(transcriptions.csv・dictionary-ja.txt)がありません。")
    prefixes = choose_test_prefixes(read_clip_names(csv_path))
    binary_dir = job_dir / "binary-duration"
    config = job_dir / CONFIG_NAME
    if config.exists():
        raise FileExistsError(f"Duration学習設定が既にあります(上書きしません): {config}")
    lines = [
        "base_config:",
        f"  - {_yaml_text(str(source / 'configs' / 'variance.yaml'))}",
        "dictionaries:",
        f"  ja: {_yaml_text(str(dictionary))}",
        "datasets:",
        f"  - raw_data_dir: {_yaml_text(str(duration))}",
        f"    speaker: {_yaml_text(speaker_id)}",
        "    spk_id: 0",
        "    language: ja",
        "    test_prefixes:" if prefixes else "    test_prefixes: []",
        *[f"      - {_yaml_text(name)}" for name in prefixes],
        f"binary_data_dir: {_yaml_text(str(binary_dir))}",
        # 音素長だけを学習する(ピッチ等は対象外)。外部のVocal Removerモデルを要求しない方式にする。
        "predict_dur: true",
        "predict_pitch: false",
        "hnsep: world",
        "",
    ]
    config.write_text("\n".join(lines), encoding="utf-8")
    return config, binary_dir, prefixes


# {
#   責務: [binarize_diffsinger_duration: Duration入力の設定を作り、公式variance binarizationを子孫ごと回収できる形で実行して、item名の記録を確認する]
#   引数: [payload: trainer_root, project_root, job_id, speaker_id, stage_timeout_seconds(省略可)]
#   戻り値: [summary: config_path, binary_data_dir, test_prefixes, item_count]
#   エラー: [bundle欠落、非0終了、タイムアウト、item名の記録欠落/個数不一致で失敗し、run_failure.jsonに種別を残す]
#   補足: [Duration trainer の起動は含まない(#96)。合成音声での受理は確認済み、実素材は未検証]
# }
def binarize_diffsinger_duration(payload: dict) -> dict:
    job_id = str(payload["job_id"])
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,100}", job_id):
        raise ValueError("歌唱jobの識別子が不正です。")
    trainer_root = Path(str(payload["trainer_root"])).resolve(strict=True)
    python = trainer_root / "runtime" / "python.exe"
    source = trainer_root / "source"
    if not python.is_file() or not (source / "scripts" / "binarize.py").is_file():
        raise FileNotFoundError("DiffSingerのbundle(runtime・binarize入口)がありません。")
    job_dir = (
        Path(str(payload["project_root"])).resolve(strict=True) / "training/diffsinger" / job_id
    ).resolve(strict=True)
    config, binary_dir, prefixes = write_duration_config(
        job_dir, source, str(payload["speaker_id"])
    )
    try:
        completed = run_reclaiming(
            [
                str(python),
                str(Path(__file__).with_name(ENTRY_NAME)),
                str(trainer_root),
                "--config",
                str(config),
            ],
            timeout=payload.get("stage_timeout_seconds"),
            cwd=str(source),
        )
        if completed.returncode != 0:
            raise RuntimeError(
                f"DiffSingerのDuration binarizationが終了コード{completed.returncode}で失敗しました。"
            )
        names = read_item_names(binary_dir)
        rows = len(read_clip_names(job_dir / "duration-input" / "transcriptions.csv"))
        if len(names) != rows - len(prefixes):
            raise RuntimeError("記録された学習itemの数が、入力から検証用を除いた数と一致しません。")
    except BaseException as error:
        record_failure(job_dir, classify(error), str(error))
        raise
    return dict(
        config_path=str(config),
        binary_data_dir=str(binary_dir),
        test_prefixes=prefixes,
        item_count=len(names),
    )
