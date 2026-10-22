from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

from prepare_diffsinger_source import LOCK_PATH, verify_source


# {
#   責務: [configure_scratch: 公式コードが使うキャッシュと一時領域を検証出力配下へ固定する]
#   引数: [output: artifacts内の検証領域]
# }
def configure_scratch(output: Path) -> None:
    locations = {"TEMP": "temp", "TMP": "temp", "HOME": "home", "USERPROFILE": "home",
                 "LOCALAPPDATA": "local-app-data", "APPDATA": "app-data", "MPLCONFIGDIR": "matplotlib",
                 "NUMBA_CACHE_DIR": "numba", "TORCH_HOME": "torch", "HF_HOME": "huggingface"}
    for name, relative in locations.items():
        location = output / "scratch" / relative
        location.mkdir(parents=True, exist_ok=True)
        os.environ[name] = str(location)
    sys.pycache_prefix = str(output / "scratch/bytecode")
    os.environ.update(HF_HUB_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1", WANDB_MODE="disabled")


# {
#   責務: [probe_features: 公式の入力読み込みと項目別特徴抽出を実音声へ適用する]
#   引数: [source: 固定ソース, raw: アプリの変換結果, output: 非公開の検証保存先]
#   戻り値: [summary: 項目数・形状と未完了の範囲]
# }
def probe_features(source: Path, raw: Path, output: Path) -> dict:
    import numpy as np
    import yaml

    sys.path.insert(0, str(source))
    from utils.hparams import hparams, override_config
    from preprocessing.acoustic_binarizer import AcousticBinarizer

    # 公式既定値を読み、追加モデルを使わない項目別の入力検証設定だけを上書きする。
    for relative in ("configs/base.yaml", "configs/acoustic.yaml"):
        override_config(hparams, yaml.safe_load((source / relative).read_text(encoding="utf-8")))
    fragment = json.loads((raw / "dataset-config.json").read_text(encoding="utf-8"))
    override_config(hparams, fragment)
    hparams.update(binary_data_dir=str(output / "binary"), work_dir=str(output / "checkpoints"),
                   pe="parselmouth", hnsep="world")
    binarizer = AcousticBinarizer()
    metadata = binarizer.load_meta_data(raw, 0, fragment["datasets"][0]["speaker"], "ja")
    expected_count = len(json.loads((raw / "provenance.json").read_text(encoding="utf-8"))["items"])
    if len(metadata) != expected_count or not metadata:
        raise ValueError("公式が読み込んだ項目数がアプリの変換記録と一致しません。")
    reports = []
    observed_phones = set()
    for index, (name, item) in enumerate(metadata.items()):
        features = binarizer.process_item(name, item, hparams["binarization_args"])
        if features is None:
            raise ValueError("公式特徴抽出で音声が除外されました。")
        length = features["length"]
        for key in ("mel", "f0", "mel2ph"):
            if len(features[key]) != length or not np.isfinite(features[key]).all():
                raise ValueError(f"公式特徴量の長さまたは数値が不正です: {key}")
        if not (features["mel2ph"].min() >= 1
                and features["mel2ph"].max() <= len(item["ph_dur"])):
            raise ValueError("melフレームと音素の対応が範囲外です。")
        observed_phones.update(item["ph_seq"])
        np.savez(output / f"features-{index}.npz", **{
            key: features[key] for key in ("mel", "f0", "mel2ph", "tokens", "ph_dur")})
        reports.append({"phone_count": len(item["ph_dur"]), "frames": length,
                        "mel_bins": features["mel"].shape[1], "positive_f0_frames": int((features["f0"] > 0).sum())})
    missing_count = len(set(range(1, len(binarizer.phoneme_dictionary))) - observed_phones)
    return {"item_count": len(reports), "items": reports, "device": str(binarizer.device),
            "unobserved_dictionary_phone_count": missing_count, "training_run": False,
            "full_binarization_run": False, "train_valid_split_verified": False,
            "openutau_render_verified": False}


# {
#   責務: [main: artifacts内で公式ソースによる音響入力検証を実行する]
#   エラー: [固定ソース不一致、不正な特徴量、既存出力は成功扱いにしない]
# }
def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--raw-data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source, raw, output = args.source.resolve(), args.raw_data.resolve(), args.output.resolve()
    artifacts = Path(__file__).resolve().parents[1] / "artifacts"
    if not output.is_relative_to(artifacts.resolve()) or output == artifacts.resolve():
        raise ValueError("検証出力はリポジトリのartifacts配下へ指定してください。")
    if any(output == path or output.is_relative_to(path) or path.is_relative_to(output) for path in (source, raw)):
        raise ValueError("検証出力と入力を別の領域にしてください。")
    lock = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    verify_source(source, lock)
    output.mkdir(parents=True, exist_ok=False)
    configure_scratch(output)
    summary = probe_features(source, raw, output)
    summary.update(source_version=lock["version"], source_commit=lock["commit"], private_data=True)
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
