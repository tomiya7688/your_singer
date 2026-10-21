from __future__ import annotations

import array
import io
import math
import subprocess
import wave
from dataclasses import dataclass
from pathlib import Path

from .voice_activity_segmentation import VoiceActivitySegmenter


# {
#   責務: [
#     WaveStats: 整音済み音声から測定した基本品質値を保持する
#   ]
#   フィールド: [
#     duration_sec: 音声の長さ（秒）
#     rms: 正規化した二乗平均平方根
#     peak: 最大振幅
#     clipping_ratio: クリッピングの割合
#     silence_ratio: 無音サンプルの割合
#     reverb_score: 簡易残響スコア
#   ]
# }
@dataclass(frozen=True)
class WaveStats:
    duration_sec: float
    rms: float
    peak: float
    clipping_ratio: float
    silence_ratio: float
    reverb_score: float


# {
#   責務: [
#     AudioQualityAnalyzer: raw・separated・cleaned音声を比較し前処理品質を評価する
#   ]
#   フィールド: []
#   処理: [
#     1: 波形特徴とノイズ・残響指標を測定する
#     2: WAV以外の入力をFFmpegでPCM16 WAVへ変換する
#     3: 品質値を0から1の範囲へまとめる
#   ]
# }
class AudioQualityAnalyzer:
    # {
    #   責務: [analyze: 分離・整音結果から音声品質指標を集計する]
    #   処理: [1: 3種類の波形統計を読み込む 2: BGM漏れ・ノイズ・総合値を計算する]
    #   引数: [raw_path: 元音声, separated_path: 分離音声, cleaned_path: 整音音声]
    #   戻り値: [quality: ノイズ・BGM漏れ・残響・クリッピング・無音の品質値]
    # }
    @classmethod
    def analyze(
        cls,
        raw_path: Path,
        separated_path: Path,
        cleaned_path: Path,
    ) -> dict[str, float]:
        cleaned_stats = cls._read_wave_stats(cleaned_path)
        raw_stats = cls._read_wave_stats(raw_path)
        separated_stats = cls._read_wave_stats(separated_path)
        music_leak = cls._clamp(separated_stats.rms / max(raw_stats.rms, 1e-9))
        noise_score = cls._estimate_noise_score(cleaned_path)

        overall = cls._clamp(
            1.0
            - (
                0.30 * noise_score
                + 0.20 * music_leak
                + 0.20 * cleaned_stats.reverb_score
                + 0.20 * min(1.0, cleaned_stats.clipping_ratio * 100.0)
                + 0.10 * cleaned_stats.silence_ratio
            )
        )
        return {
            "overall": overall,
            "noise": noise_score,
            "music_leak": music_leak,
            "reverb": cleaned_stats.reverb_score,
            "clipping": cleaned_stats.clipping_ratio,
            "silence": cleaned_stats.silence_ratio,
        }

    # {
    #   責務: [_read_wave_stats: 音声ファイルから長さ・振幅・クリッピング・無音・残響を測る]
    #   処理: [1: PCM16 WAVを直接読む 2: 他形式をFFmpegでPCM16へ変換する 3: 波形統計を集計する]
    #   引数: [path: 品質を測定する音声ファイル]
    #   戻り値: [stats: 波形の基本品質値]
    #   エラー: [PCM16以外のWAVの場合にValueErrorを送出する]
    # }
    @classmethod
    def _read_wave_stats(cls, path: Path) -> WaveStats:
        # PCM16 WAVはそのまま読み、圧縮音声や別形式のWAVだけをFFmpegへ渡す。
        try:
            with wave.open(str(path), "rb") as wav:
                if wav.getsampwidth() == 2:
                    return cls._summarize_wave(wav)
        except wave.Error:
            pass

        # FFmpegの標準出力を使い、一時ファイルや利用者の入力ファイルを作らず変換する。
        decoded = subprocess.run(
            [
                "ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error",
                "-i", str(path), "-f", "wav", "-acodec", "pcm_s16le",
                "-ac", "2", "-ar", "48000", "pipe:1",
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        if decoded.returncode != 0:
            raise RuntimeError("音声品質解析用のPCM16 WAV変換に失敗しました。")

        with wave.open(io.BytesIO(decoded.stdout), "rb") as wav:
            return cls._summarize_wave(wav)

    # {
    #   責務: [_summarize_wave: 開いたPCM16 WAVから基本品質値を集計する]
    #   処理: [1: サンプル形式を検証する 2: チャンネルを平均して波形統計を作る]
    #   引数: [wav: 読み込み済みPCM16 WAV]
    #   戻り値: [stats: 長さ、音量、クリッピング、無音、残響の統計]
    #   エラー: [PCM16以外の入力の場合にValueErrorを送出する]
    # }
    @classmethod
    def _summarize_wave(cls, wav: wave.Wave_read) -> WaveStats:
        # 変換経路が常にPCM16を返すことを確認してから整数サンプルとして読む。
        if wav.getsampwidth() != 2:
            raise ValueError("PCM16 WAVのみ品質解析できます。")

        frame_rate = wav.getframerate()
        channels = wav.getnchannels()
        frames = wav.getnframes()
        samples = array.array("h", wav.readframes(frames))

        if channels > 1:
            mono = array.array("h")
            for index in range(0, len(samples), channels):
                mono.append(int(sum(samples[index : index + channels]) / channels))
            samples = mono

        sample_count = max(1, len(samples))
        peak = max((abs(value) for value in samples), default=0)
        clipping = sum(1 for value in samples if abs(value) >= 32700) / sample_count
        silence = sum(1 for value in samples if abs(value) <= 180) / sample_count
        return WaveStats(
            duration_sec=frames / max(1, frame_rate),
            rms=VoiceActivitySegmenter.rms(samples) / 32768.0,
            peak=peak / 32768.0,
            clipping_ratio=clipping,
            silence_ratio=silence,
            reverb_score=cls._estimate_reverb(samples, frame_rate),
        )

    # {
    #   責務: [_estimate_noise_score: 音量窓の下位10%と上位75%からノイズ比を推定する]
    #   処理: [1: 波形を短いブロックへ分ける 2: ノイズ床を発話レベルで正規化する]
    #   引数: [path: 整音済み音声ファイル]
    #   戻り値: [score: 0から1のノイズ注意値]
    # }
    @classmethod
    def _estimate_noise_score(cls, path: Path) -> float:
        with wave.open(str(path), "rb") as wav:
            samples = array.array("h", wav.readframes(wav.getnframes()))
        if not samples:
            return 1.0

        block_size = max(1, len(samples) // 200)
        block_rms = [
            VoiceActivitySegmenter.rms(samples[index : index + block_size])
            for index in range(0, len(samples), block_size)
        ]
        block_rms.sort()
        noise_floor = block_rms[max(0, int(len(block_rms) * 0.10) - 1)]
        speech_level = block_rms[max(0, int(len(block_rms) * 0.75) - 1)]
        if speech_level <= 1:
            return 1.0
        return cls._clamp(noise_floor / speech_level * 3.0)

    # {
    #   責務: [_estimate_reverb: 音量包絡の減衰尾部から簡易残響値を推定する]
    #   処理: [1: 200Hz相当の間隔で包絡を作る 2: ピーク後の減衰区間を集計する]
    #   引数: [samples: PCM音声サンプル, frame_rate: サンプルレート]
    #   戻り値: [score: 0から1の残響注意値]
    # }
    @classmethod
    def _estimate_reverb(cls, samples: array.array, frame_rate: int) -> float:
        if len(samples) < frame_rate // 2:
            return 0.0

        stride = max(1, frame_rate // 200)
        envelope = [abs(samples[index]) for index in range(0, len(samples), stride)]
        if len(envelope) < 20:
            return 0.0

        peak = max(envelope)
        if peak <= 0:
            return 0.0

        threshold = peak * 0.35
        tail = 0
        tail_total = 0
        active = False
        for value in envelope:
            if value >= threshold:
                active = True
                tail = 0
            elif active:
                tail += 1
                tail_total += 1
                if tail > 20:
                    active = False
        return cls._clamp(tail_total / max(1, len(envelope)) * 2.0)

    # {
    #   責務: [_clamp: 指標値を0から1の範囲へ制限する]
    #   処理: [1: floatへ変換し下限と上限を適用する]
    #   引数: [value: 制限する指標値]
    #   戻り値: [clamped: 0以上1以下の数値]
    # }
    @staticmethod
    def _clamp(value: float) -> float:
        return max(0.0, min(1.0, float(value)))
