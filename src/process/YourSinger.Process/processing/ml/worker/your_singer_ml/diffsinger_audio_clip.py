from __future__ import annotations

import hashlib
import math
import tempfile
import re
import wave
from pathlib import Path

from .verified_file_source import open_verified_file


# {
#   責務: [DiffSingerAudioClip: 実測音素時刻とPCM音声を照合して学習範囲を切り出す]
#   フィールド: [path: 元音声, starts: 開始時刻, ends: 終了時刻, phones: 音素列]
#   処理: [1: 対応と連続性を検証する 2: PCMサンプル境界へ丸めて保存する]
# }
class DiffSingerAudioClip:
    # {
    #   責務: [__init__: 音素と境界の対応を検証する]
    #   引数: [item: 共通データから作られた歌唱学習項目, path: 検証済み音声パス]
    #   エラー: [旧入力、推定時刻、不正値、不連続な区間を拒否する]
    # }
    def __init__(self, item: dict, path: Path):
        self.path = path
        measurement = item.get("alignment_measurement")
        self.audio_sha256 = (
            measurement.get("audio_sha256") if isinstance(measurement, dict) else None
        )
        if not isinstance(self.audio_sha256, str) or not re.fullmatch(
            r"[0-9a-f]{64}", self.audio_sha256
        ):
            raise ValueError("音声の測定ハッシュがありません。再解析してください。")
        self.phones = item.get("phonemes")
        self.starts = item.get("phoneme_starts_sec")
        self.ends = item.get("phoneme_ends_sec")
        durations = item.get("durations_sec")
        if item.get("alignment_method") != "mfa-japanese-phone-forced-alignment-v1":
            raise ValueError(
                "歌唱入力に実測forced alignmentがありません。入力を再生成してください。"
            )
        sequences = (self.phones, self.starts, self.ends, durations)
        if (
            any(not isinstance(seq, list) or not seq for seq in sequences)
            or len({len(seq) for seq in sequences}) != 1
        ):
            raise ValueError("歌唱入力の音素・開始時刻・終了時刻・長さが対応していません。")
        for index, (phone, start, end, duration) in enumerate(zip(*sequences, strict=True)):
            if not isinstance(phone, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", phone):
                raise ValueError("歌唱入力の音素表記が不正です。")
            if any(
                type(number) not in (int, float) or not math.isfinite(number)
                for number in (start, end, duration)
            ):
                raise ValueError("音素時刻に有限な数値が必要です。")
            if start < 0 or end <= start or not math.isclose(end - start, duration, abs_tol=1e-9):
                raise ValueError("音素境界と音素長が一致していません。")
            # 未解析の隙間へ無音ラベルを作ったり、後続音素を前へ詰めたりしない。
            if index and abs(start - self.ends[index - 1]) > 1e-6:
                raise ValueError("音素間に未解析の隙間または重なりがあります。再解析してください。")

    # {
    #   責務: [write: 先頭・末尾の未対応音声を除き音素列に対応するPCMを保存する]
    #   引数: [destination: 新規WAVの保存先]
    #   戻り値: [crop: 元音声内のサンプル境界と生成音声長]
    #   エラー: [PCM16モノラル以外、範囲外、空音声、途中切れを拒否する]
    # }
    def write(self, destination: Path) -> dict:
        # { 処理: [同じ入力ハンドルからコピーとhashを作り、一致した一時コピーを切り出す] }
        with tempfile.TemporaryFile(dir=destination.parent) as snapshot:
            digest = hashlib.sha256()
            size = 0
            with open_verified_file(self.path) as source:
                while chunk := source.read(1024 * 1024):
                    size += len(chunk)
                    if size > 1024 * 1024 * 1024:
                        raise ValueError("音声がサイズ上限を超えています。")
                    digest.update(chunk)
                    snapshot.write(chunk)
            if digest.hexdigest() != self.audio_sha256:
                raise ValueError("音声が測定後に変更されました。再解析してください。")
            snapshot.seek(0)
            return self._write_snapshot(snapshot, destination)

    # { 責務: [_write_snapshot: 検証済み音声の同じバイト列から必要範囲を保存する] }
    def _write_snapshot(self, snapshot, destination: Path) -> dict:
        with wave.open(snapshot, "rb") as original:
            if (original.getnchannels(), original.getsampwidth(), original.getcomptype()) != (
                1,
                2,
                "NONE",
            ):
                raise ValueError("歌唱学習にはPCM16モノラルWAVが必要です。")
            sample_rate = original.getframerate()
            start_sample = round(self.starts[0] * sample_rate)
            end_sample = round(self.ends[-1] * sample_rate)
            if end_sample > original.getnframes() or not 0 <= start_sample < end_sample:
                raise ValueError("実測音素区間が音声範囲外です。")
            original.setpos(start_sample)
            # { 処理: [長い区間を固定サイズで転送する] }
            remaining = end_sample - start_sample
            with wave.open(str(destination), "wb") as output:
                output.setparams(original.getparams())
                while remaining:
                    frame_count = min(remaining, 65536)
                    samples = original.readframes(frame_count)
                    if len(samples) != frame_count * 2:
                        raise ValueError("音声が途中で切れています。")
                    output.writeframesraw(samples)
                    remaining -= frame_count
        return {
            "start_sample": start_sample,
            "end_sample": end_sample,
            "sample_rate": sample_rate,
            "duration_sec": (end_sample - start_sample) / sample_rate,
        }
