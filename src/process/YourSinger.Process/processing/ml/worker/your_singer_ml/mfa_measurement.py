from __future__ import annotations

import hashlib
import json
import stat
import tempfile
from collections.abc import Iterator
from pathlib import Path

MAX_ALIGNMENT_AUDIO_BYTES = 1024 * 1024 * 1024


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
#   エラー: [通常ファイル以外、1GiB超、読込中のサイズ変更を拒否する]
# }
def audio_fingerprint(path: Path) -> str:
    digest = hashlib.sha256()
    for chunk in _bounded_audio_chunks(path):
        digest.update(chunk)
    return digest.hexdigest()


# {
#   責務: [copy_audio_snapshot: MFAが読む音声を専用一時領域へ固定し、その内容のhashを返す]
#   引数: [source: 元音声, destination: 新規の私的なコピー先]
#   戻り値: [sha256: コピーへ書き出した音声のhash]
#   エラー: [不正入力・サイズ変更・保存失敗を伝える。既存コピーは上書きしない]
# }
def copy_audio_snapshot(source: Path, destination: Path) -> str:
    digest = hashlib.sha256()
    with destination.open("xb") as output:
        for chunk in _bounded_audio_chunks(source):
            output.write(chunk)
            digest.update(chunk)
    return digest.hexdigest()


# {
#   責務: [_bounded_audio_chunks: 通常音声ファイルを開始時のサイズだけ分割して読む]
#   引数: [path: 1GiB以内の元音声]
#   戻り値: [chunks: 最大1MiBずつの音声バイト列]
#   エラー: [特殊ファイル・過大入力・読込中の伸縮を拒否する]
# }
def _bounded_audio_chunks(path: Path) -> Iterator[bytes]:
    metadata = path.stat()
    if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > MAX_ALIGNMENT_AUDIO_BYTES:
        raise ValueError("MFA音声は1GiB以内の通常ファイルを指定してください。")
    # 読み始めたサイズに制限し、書き足し続ける入力を最後まで追いかけない。
    with path.open("rb") as stream:
        remaining = metadata.st_size
        while remaining:
            chunk = stream.read(min(remaining, 1024 * 1024))
            if not chunk:
                raise RuntimeError("MFA音声のサイズが読込中に変更されました。")
            yield chunk
            remaining -= len(chunk)
        if stream.read(1):
            raise RuntimeError("MFA音声のサイズが読込中に変更されました。")
