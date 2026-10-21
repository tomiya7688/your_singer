from __future__ import annotations

import math
import re
import wave
from pathlib import Path


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
        self.phones = item.get("phonemes")
        self.starts = item.get("phoneme_starts_sec")
        self.ends = item.get("phoneme_ends_sec")
        durations = item.get("durations_sec")
        if item.get("alignment_method") != "mfa-japanese-phone-forced-alignment-v1":
            raise ValueError("歌唱入力に実測forced alignmentがありません。入力を再生成してください。")
        sequences = (self.phones, self.starts, self.ends, durations)
        if (any(not isinstance(seq, list) or not seq for seq in sequences)
                or len({len(seq) for seq in sequences}) != 1):
            raise ValueError("歌唱入力の音素・開始時刻・終了時刻・長さが対応していません。")
        for index, (phone, start, end, duration) in enumerate(zip(*sequences, strict=True)):
            if not isinstance(phone, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", phone):
                raise ValueError("歌唱入力の音素表記が不正です。")
            if any(type(number) not in (int, float) or not math.isfinite(number)
                   for number in (start, end, duration)):
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
        with wave.open(str(self.path), "rb") as original:
            if (original.getnchannels(), original.getsampwidth(), original.getcomptype()) != (1, 2, "NONE"):
                raise ValueError("歌唱学習にはPCM16モノラルWAVが必要です。")
            sample_rate = original.getframerate()
            start_sample = round(self.starts[0] * sample_rate)
            end_sample = round(self.ends[-1] * sample_rate)
            if end_sample > original.getnframes() or not 0 <= start_sample < end_sample:
                raise ValueError("実測音素区間が音声範囲外です。")
            original.setpos(start_sample)
            samples = original.readframes(end_sample - start_sample)
            if len(samples) != (end_sample - start_sample) * 2:
                raise ValueError("音声が途中で切れています。")
            parameters = original.getparams()
        with wave.open(str(destination), "wb") as output:
            output.setparams(parameters)
            output.writeframes(samples)
        return {"start_sample": start_sample, "end_sample": end_sample,
                "sample_rate": sample_rate, "duration_sec": (end_sample - start_sample) / sample_rate}
