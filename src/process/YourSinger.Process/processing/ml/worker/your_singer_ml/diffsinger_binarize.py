from __future__ import annotations

import json
import math
import re
from pathlib import Path

from .diffsinger_initialization import _yaml_text
from .diffsinger_weighted_sampler import NAMES_FILE, read_item_names
from .process_tree import classify, record_failure, run_reclaiming

ENTRY_NAME = "diffsinger_binarize_entry.py"
CONFIG_NAME = "acoustic-training.yaml"
# 検証用に取り分ける割合と上限。少数素材でも学習用を1件以上残す。
VALID_FRACTION = 0.1
MAX_VALID = 5


# {
#   責務: [choose_test_prefixes: 検証用に取り分ける区間名を、名前の順で決定的に選ぶ]
#   引数: [names: 音響学習入力のクリップ名]
#   戻り値: [prefixes: 検証用の名前。1件以下の入力では空(学習用を残す)]
#   補足: [公式の test_prefixes と同じく名前で指定する。学習用が必ず1件以上残る]
# }
def choose_test_prefixes(names: list[str]) -> list[str]:
    ordered = sorted(names)
    if len(ordered) < 2:
        return []
    count = min(MAX_VALID, max(1, math.floor(len(ordered) * VALID_FRACTION)), len(ordered) - 1)
    step = len(ordered) / count
    return [ordered[int(index * step)] for index in range(count)]


# {
#   責務: [write_training_config: 音響学習入力から、公式のbinarizationと学習が読む設定(overlay)を作る]
#   処理: [固定ソースの configs/acoustic.yaml を base_config に取り、辞書・データセット・検証分割・binary_data_dirだけを指定する]
#   戻り値: [config_path, binary_dir, test_prefixes]
#   エラー: [設定が既にある(上書きしない)、入力のCSV/辞書が無いときに失敗する]
# }
def write_training_config(
    job_dir: Path, source: Path, speaker_id: str
) -> tuple[Path, Path, list[str]]:
    acoustic = job_dir / "acoustic-input"
    csv_path, dictionary = acoustic / "transcriptions.csv", acoustic / "dictionary-ja.txt"
    if not csv_path.is_file() or not dictionary.is_file():
        raise FileNotFoundError("音響学習入力(transcriptions.csv・dictionary-ja.txt)がありません。")
    names = [
        line.split(",", 1)[0]
        for line in csv_path.read_text(encoding="utf-8").splitlines()[1:]
        if line
    ]
    prefixes = choose_test_prefixes(names)
    binary_dir = job_dir / "binary"
    config = job_dir / CONFIG_NAME
    if config.exists():
        raise FileExistsError(f"学習設定が既にあります(上書きしません): {config}")
    lines = [
        "base_config:",
        f"  - {_yaml_text(str(source / 'configs' / 'acoustic.yaml'))}",
        "dictionaries:",
        f"  ja: {_yaml_text(str(dictionary))}",
        "datasets:",
        f"  - raw_data_dir: {_yaml_text(str(acoustic))}",
        f"    speaker: {_yaml_text(speaker_id)}",
        "    spk_id: 0",
        "    language: ja",
        "    test_prefixes:" if prefixes else "    test_prefixes: []",
        *[f"      - {_yaml_text(name)}" for name in prefixes],
        f"binary_data_dir: {_yaml_text(str(binary_dir))}",
        # 重みmanifestは元のクリップ1件ごとに持つため、増幅した別名のitemを作らない。
        "augmentation_args:",
        "  random_pitch_shifting:",
        "    enabled: false",
        "  fixed_pitch_shifting:",
        "    enabled: false",
        "  random_time_stretching:",
        "    enabled: false",
        "",
    ]
    config.write_text("\n".join(lines), encoding="utf-8")
    return config, binary_dir, prefixes


# {
#   責務: [binarize_diffsinger: 学習設定を作り、公式binarizationを子孫ごと回収できる形で実行して、item名の記録を確認する]
#   引数: [payload: trainer_root, project_root, job_id, speaker_id, stage_timeout_seconds(省略可)]
#   戻り値: [summary: config_path, binary_data_dir, test_prefixes, item_count]
#   エラー: [bundle欠落、非0終了、タイムアウト、item名の記録欠落/個数不一致で失敗し、run_failure.jsonに種別を残す]
#   補足: [実GPU/実モデルでの公式binarization実行は未検証。vocoderなどの外部モデルの取得は含まない]
# }
def binarize_diffsinger(payload: dict) -> dict:
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
    config, binary_dir, prefixes = write_training_config(
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
                "--exp_name",
                job_id,
            ],
            timeout=payload.get("stage_timeout_seconds"),
            cwd=str(source),
        )
        if completed.returncode != 0:
            raise RuntimeError(
                f"DiffSingerのbinarizationが終了コード{completed.returncode}で失敗しました。"
            )
        names = read_item_names(binary_dir)
        rows = (
            len(
                (job_dir / "acoustic-input" / "transcriptions.csv")
                .read_text(encoding="utf-8")
                .splitlines()
            )
            - 1
        )
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
