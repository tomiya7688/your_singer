from __future__ import annotations

import csv
import hashlib
import json
import re
import shutil
import tempfile
from pathlib import Path

from .diffsinger_audio_clip import DiffSingerAudioClip


# {
#   責務: [DiffSingerAcousticInput: 歌唱jobを公式AcousticBinarizerの入力形式へ変換する]
#   フィールド: [dataset_root: job入力領域, project_root: 私的素材の作業領域]
#   処理: [1: jobと素材の対応を照合する 2: 音声とCSVを作る 3: 完成後に配置する]
# }
class DiffSingerAcousticInput:
    # {
    #   責務: [__init__: job識別子・fingerprint・補正設定と保存先を照合する]
    #   引数: [payload: C#側の歌唱jobと作業領域]
    #   エラー: [領域外、旧入力、異なるjob、既存出力を拒否する]
    # }
    def __init__(self, payload: dict):
        self.project_root = Path(payload["project_root"]).resolve(strict=True)
        job_id = payload["job_id"]
        if not isinstance(job_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,100}", job_id):
            raise ValueError("歌唱jobの識別子が不正です。")
        self.dataset_root = (self.project_root / "training/diffsinger" / job_id).resolve(strict=True)
        if not self.dataset_root.is_relative_to(self.project_root):
            raise ValueError("歌唱jobが作業領域外です。")
        self.document = json.loads(self._require_file(
            str(self.dataset_root / "dataset.json")).read_text(encoding="utf-8-sig"))
        if self.document.get("stage_version") != "v1-10.4":
            raise ValueError("音素境界付きの歌唱入力を再生成してください。")
        for key in ("job_id", "dataset_fingerprint", "auto_correction_enabled", "speaker_id"):
            if key not in payload or self.document.get(key) != payload[key]:
                raise ValueError(f"歌唱入力とjobの設定が一致しません: {key}")
        if (type(payload["auto_correction_enabled"]) is not bool
                or type(self.document.get("auto_correction_enabled")) is not bool):
            raise ValueError("補正設定には真偽値が必要です。")
        if any(not isinstance(payload[key], str) or not payload[key]
               for key in ("dataset_fingerprint", "speaker_id")):
            raise ValueError("歌唱jobにfingerprintと話者IDが必要です。")
        self.destination = self.dataset_root / "acoustic-input"
        if self.destination.exists() or self.destination.is_symlink():
            raise FileExistsError("歌唱音響入力は作成済みです。新しいjobを作成してください。")
        self.items = self._validate_items()

    # {
    #   責務: [_require_file: 私的素材を作業領域内の実ファイルへ解決する]
    #   引数: [reference: 素材または特徴量の保存パス]
    #   戻り値: [path: 絶対パス]
    # }
    def _require_file(self, reference: str) -> Path:
        path = (self.project_root / reference).resolve(strict=True)
        if not path.is_relative_to(self.project_root) or not path.is_file():
            raise ValueError("歌唱学習の入力ファイルが作業領域外です。")
        return path

    # {
    #   責務: [_validate_items: 全入力の重複・音声・実測境界を出力開始前に検証する]
    #   戻り値: [clips: 学習項目と検証済み切り出し設定]
    # }
    def _validate_items(self) -> list[tuple[dict, DiffSingerAudioClip]]:
        items = self.document.get("items")
        if not isinstance(items, list) or not items:
            raise ValueError("歌唱学習項目がありません。")
        seen_ids = set()
        clips = []
        for item in items:
            segment_id = item.get("segment_id")
            if not isinstance(segment_id, str) or not segment_id or segment_id in seen_ids:
                raise ValueError("歌唱segment識別子が空または重複しています。")
            seen_ids.add(segment_id)
            for feature in ("f0_feature_path", "energy_feature_path"):
                self._require_file(item[feature])
            clips.append((item, DiffSingerAudioClip(item, self._require_file(item["audio_path"]))))
        return clips

    # {
    #   責務: [build: 公式CSV・切り出し音声・音素辞書と変換記録を作る]
    #   戻り値: [summary: 入力の参照、件数、学習未実行の状態]
    #   エラー: [失敗時は自身の一時領域だけを破棄し、元入力を残す]
    # }
    def build(self) -> dict:
        staging = Path(tempfile.mkdtemp(prefix=".acoustic-", dir=self.dataset_root)).resolve()
        try:
            (staging / "wavs").mkdir()
            records = self._write_clips(staging)
            phones = sorted({phone for item, _ in self.items for phone in item["phonemes"]})
            (staging / "dictionary-ja.txt").write_text(
                "".join(f"{phone}\t{phone}\n" for phone in phones), encoding="utf-8")
            # 学習・検証分割と完全な発音辞書は、Trainer準備時に別途検証する。
            fragment = {"dictionaries": {"ja": str(self.destination / "dictionary-ja.txt")},
                        "datasets": [{"raw_data_dir": str(self.destination),
                                      "speaker": self.document["speaker_id"], "spk_id": 0, "language": "ja"}]}
            (staging / "dataset-config.json").write_text(
                json.dumps(fragment, ensure_ascii=False, indent=2), encoding="utf-8")
            provenance = {key: self.document[key] for key in (
                "stage_version", "job_id", "speaker_id", "dataset_fingerprint", "auto_correction_enabled")}
            provenance.update(schema="your-singer-diffsinger-acoustic-v1", items=records,
                              training_run=False, source_audio_unchanged=True)
            (staging / "provenance.json").write_text(
                json.dumps(provenance, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
            staging.rename(self.destination)
        finally:
            # stagingはこの関数がjob配下に作った絶対パスだけを削除対象とする。
            if staging.is_relative_to(self.dataset_root) and staging.exists():
                shutil.rmtree(staging)
        return {"raw_data_dir": str(self.destination), "item_count": len(self.items),
                "dictionary_path": str(self.destination / "dictionary-ja.txt"), "training_run": False}

    # {
    #   責務: [_write_clips: 各音声を実測範囲で切り出しCSV行と由来を保存する]
    #   引数: [staging: job内の専用一時領域]
    #   戻り値: [records: 元segmentとサンプル境界の対応]
    # }
    def _write_clips(self, staging: Path) -> list[dict]:
        records = []
        with (staging / "transcriptions.csv").open("w", encoding="utf-8", newline="") as csv_file:
            writer = csv.writer(csv_file)
            writer.writerow(["name", "ph_seq", "ph_dur"])
            for item, clip in self.items:
                name = "segment_" + hashlib.sha256(item["segment_id"].encode("utf-8")).hexdigest()
                crop = clip.write(staging / "wavs" / f"{name}.wav")
                writer.writerow([name, " ".join(clip.phones),
                                 " ".join(format(end - start, ".12g")
                                          for start, end in zip(clip.starts, clip.ends, strict=True))])
                records.append({"name": name, "segment_id": item["segment_id"], "crop": crop})
        return records


# {
#   責務: [prepare_diffsinger_acoustic: worker要求から音響学習入力を生成する]
#   引数: [payload: 歌唱jobの識別情報]
#   戻り値: [summary: 生成した公式入力の参照]
# }
def prepare_diffsinger_acoustic(payload: dict) -> dict:
    return DiffSingerAcousticInput(payload).build()
