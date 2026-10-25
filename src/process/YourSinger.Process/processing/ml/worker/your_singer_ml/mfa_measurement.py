from __future__ import annotations

import hashlib
import json
import tempfile
from pathlib import Path


# {
#   責務: [MfaMeasurementStore: 検証済みMFA測定の音素・無音・未観測区間を私的領域に保存する]
#   フィールド: [root: Voice Project内の測定記録保存先]
#   処理: [1: 元ラベルと未観測区間を分ける 2: 測定条件とともに新しい記録を公開する]
# }
class MfaMeasurementStore:
    # {
    #   責務: [__init__: 測定記録を保存する領域を保持する]
    #   引数: [root: alignment配下のmeasurements領域]
    # }
    def __init__(self, root: Path):
        if root.is_symlink() or not root.resolve().is_relative_to(root.parent.resolve()):
            raise ValueError("MFA測定記録はalignment領域内へ保存してください。")
        self.root = root

    # {
    #   責務: [save: 検証済み結果を既存記録へ上書きせず保存する]
    #   引数: [document: 検証済みMFA JSON, timings: 対応済み音素, duration_sec: 音声長,
    #           audio_sha256: 実行前後で一致した音声hash, conditions: 固定モデルと測定方式]
    #   戻り値: [path: 完成した測定記録の保存先]
    #   エラー: [保存できない場合は未完成記録を公開せず失敗する]
    # }
    def save(
        self,
        document: dict,
        timings: list[dict],
        duration_sec: float,
        audio_sha256: str,
        conditions: dict,
    ) -> Path:
        entries = document["tiers"]["phones"]["entries"]
        # silはMFAの分類結果であり、SP・APの正解ラベルへ自動変換しない。
        intervals = [
            {"start_sec": start, "end_sec": end, "label": label} for start, end, label in entries
        ]
        measurement = {
            "schema_version": 1,
            "audio_sha256": audio_sha256,
            "duration_sec": duration_sec,
            "conditions": conditions,
            "phonemes": timings,
            "mfa_intervals": intervals,
            "silence_intervals": [entry for entry in intervals if entry["label"] == "sil"],
            "unlabelled_intervals": [entry for entry in intervals if entry["label"] == ""],
            "unobserved_intervals": self._find_gaps(intervals, duration_sec),
            "confidence_calibrated": False,
            "breath_classification_run": False,
        }
        encoded = json.dumps(measurement, ensure_ascii=False, indent=2, allow_nan=False)
        self.root.mkdir(parents=True, exist_ok=True)
        # 一意な専用領域を確保し、完成JSONだけをrenameで公開する。
        with tempfile.TemporaryDirectory(prefix=".measurement-", dir=self.root) as staging:
            temporary = Path(staging) / "measurement.json"
            temporary.write_text(encoded, encoding="utf-8")
            destination = self.root / (Path(staging).name.removeprefix(".") + ".json")
            if destination.exists():
                raise FileExistsError("MFA測定記録の保存先が既に存在します。")
            temporary.rename(destination)
        return destination

    # {
    #   責務: [_find_gaps: tierが存在しない時間を無音と区別して記録する]
    #   引数: [intervals: 検証済みの元区間, duration_sec: 音声長]
    #   戻り値: [gaps: 始端・中間・終端の未観測時間]
    # }
    @staticmethod
    def _find_gaps(intervals: list[dict], duration_sec: float) -> list[dict]:
        gaps = []
        previous_end = 0.0
        for interval in intervals:
            if interval["start_sec"] > previous_end + 1e-6:
                gaps.append({"start_sec": previous_end, "end_sec": interval["start_sec"]})
            previous_end = interval["end_sec"]
        if duration_sec > previous_end + 1e-6:
            gaps.append({"start_sec": previous_end, "end_sec": duration_sec})
        return gaps


# {
#   責務: [audio_fingerprint: 音声の同一性をファイル名や更新日時に依存せず確認する]
#   引数: [path: MFAへ渡す元音声]
#   戻り値: [sha256: 音声ファイルのhash]
# }
def audio_fingerprint(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()
