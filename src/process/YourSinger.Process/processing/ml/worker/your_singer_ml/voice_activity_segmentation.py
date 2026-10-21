from __future__ import annotations

import array
import math
import wave
from collections.abc import Callable
from pathlib import Path


# {
#   責務: [
#     VoiceActivitySegmenter: 音量窓を使って発話区間を検出しPCM WAVへ書き出す
#   ]
#   フィールド: [
#     WINDOW_MS: 音量を測る窓の長さ
#     MIN_VOICE_SEC: 出力する区間の最小長
#     MAX_GAP_SEC: ひとつの区間へ結合する無音の最大長
#   ]
#   処理: [
#     1: PCM16 mono WAVの短時間RMSを測定する
#     2: 閾値と無音間隔から発話区間を決める
#     3: ffmpegで区間音声を書き出す
#   ]
# }
class VoiceActivitySegmenter:
    WINDOW_MS = 30
    MIN_VOICE_SEC = 0.30
    MAX_GAP_SEC = 0.25
    MIN_RMS = 350.0
    RMS_FLOOR_MULTIPLIER = 3.0

    # {
    #   責務: [detect_and_export: 音声から発話区間を検出して個別WAVへ書き出す]
    #   処理: [1: 音量窓を測る 2: 発話区間を選ぶ 3: 各区間を書き出す]
    #   引数: [cleaned_path: 整音済みWAV, segments_dir: 出力先, source_id: 音源ID, workspace: 相対パス基準, run_command: 外部コマンド実行関数]
    #   戻り値: [segments: 開始・終了時刻と相対パスを持つsegment一覧]
    #   エラー: [WAV形式が不正、またはffmpegの区間出力に失敗した場合に失敗する]
    # }
    @classmethod
    def detect_and_export(
        cls,
        cleaned_path: Path,
        segments_dir: Path,
        source_id: str,
        workspace: Path,
        run_command: Callable[[list[str]], None],
    ) -> list[dict]:
        rms_values = cls._measure_rms_windows(cleaned_path)
        if not rms_values:
            return []

        window_sec = cls.WINDOW_MS / 1000.0
        ranges = cls._find_voice_ranges(rms_values, window_sec)
        return cls._export_segments(
            ranges,
            cleaned_path,
            segments_dir,
            source_id,
            workspace,
            run_command,
        )

    # {
    #   責務: [_measure_rms_windows: PCM16 mono WAVの短時間RMS一覧を作る]
    #   処理: [1: WAVのチャンネルとsample幅を検証する 2: 30ms窓ごとのRMSを測る]
    #   引数: [path: 測定する整音済みWAV]
    #   戻り値: [rms_values: 窓ごとのRMS値]
    #   エラー: [入力がmono PCM16でない場合にValueErrorを送出する]
    # }
    @classmethod
    def _measure_rms_windows(cls, path: Path) -> list[float]:
        with wave.open(str(path), "rb") as wav:
            if wav.getnchannels() != 1 or wav.getsampwidth() != 2:
                raise ValueError("VAD入力はmono PCM16 WAVである必要があります。")

            frame_rate = wav.getframerate()
            samples = array.array("h", wav.readframes(wav.getnframes()))

        window_samples = max(1, int(frame_rate * cls.WINDOW_MS / 1000))
        return [
            cls.rms(samples[start : start + window_samples])
            for start in range(0, len(samples), window_samples)
        ]

    # {
    #   責務: [_find_voice_ranges: RMS値から結合済み発話時刻を選ぶ]
    #   処理: [1: 下位20パーセンタイルから閾値を決める 2: 隙間を結合して時刻へ変換する]
    #   引数: [rms_values: 窓ごとのRMS値, window_sec: 窓の長さ（秒）]
    #   戻り値: [ranges: 発話区間の開始・終了時刻]
    # }
    @classmethod
    def _find_voice_ranges(
        cls,
        rms_values: list[float],
        window_sec: float,
    ) -> list[tuple[float, float]]:
        sorted_rms = sorted(rms_values)
        floor_index = max(0, int(len(sorted_rms) * 0.20) - 1)
        threshold = max(cls.MIN_RMS, sorted_rms[floor_index] * cls.RMS_FLOOR_MULTIPLIER)
        voiced_windows = [rms_value >= threshold for rms_value in rms_values]
        return cls._merge_voiced_windows(voiced_windows, window_sec, cls.MAX_GAP_SEC)

    # {
    #   責務: [_export_segments: 有効な発話区間を個別PCM16 WAVへ書き出す]
    #   処理: [1: 短すぎる区間を除外する 2: 残りをffmpegで切り出しsegment情報を返す]
    #   引数: [ranges: 発話区間時刻, cleaned_path: 入力WAV, segments_dir: 出力先, source_id: 音源ID, workspace: 相対パス基準, run_command: 外部コマンド実行関数]
    #   戻り値: [segments: 書き出したsegment一覧]
    #   エラー: [ffmpegが区間を切り出せない場合に実行関数から失敗する]
    #   副作用: [segments_dirへPCM16 WAVを書き出す]
    # }
    @classmethod
    def _export_segments(
        cls,
        ranges: list[tuple[float, float]],
        cleaned_path: Path,
        segments_dir: Path,
        source_id: str,
        workspace: Path,
        run_command: Callable[[list[str]], None],
    ) -> list[dict]:
        segments: list[dict] = []
        for start_sec, end_sec in ranges:
            if end_sec - start_sec < cls.MIN_VOICE_SEC:
                continue

            segment_id = f"seg_{source_id}_{len(segments):05d}"
            output_path = segments_dir / f"{segment_id}.wav"
            run_command(
                [
                    "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                    "-ss", f"{start_sec:.3f}", "-to", f"{end_sec:.3f}",
                    "-i", str(cleaned_path), "-c:a", "pcm_s16le", str(output_path),
                ]
            )

            total_windows = max(1, round((end_sec - start_sec) / (cls.WINDOW_MS / 1000.0)))
            segments.append(
                {
                    "segment_id": segment_id,
                    "start_sec": start_sec,
                    "end_sec": end_sec,
                    "audio_path": cls._relative(output_path, workspace),
                    "voiced_probability": 1.0,
                    "silence_ratio": 1.0 / total_windows,
                }
            )
        return segments

    # {
    #   責務: [_merge_voiced_windows: 最大無音間隔以下の発話窓を同一区間へ結合する]
    #   処理: [1: 発話窓の先頭と最後を追跡し、許容間隔を超えた箇所で区間を閉じる]
    #   引数: [voiced: 窓ごとの発話判定, window_sec: 窓の長さ（秒）, max_gap_sec: 許容無音長]
    #   戻り値: [ranges: 発話区間の開始・終了時刻]
    # }
    @staticmethod
    def _merge_voiced_windows(
        voiced: list[bool],
        window_sec: float,
        max_gap_sec: float,
    ) -> list[tuple[float, float]]:
        max_gap_windows = max(1, round(max_gap_sec / window_sec))
        ranges: list[tuple[int, int]] = []
        start: int | None = None
        last_voice: int | None = None

        for index, is_voiced in enumerate(voiced):
            if is_voiced:
                if start is None:
                    start = index
                last_voice = index
                continue

            if start is not None and last_voice is not None and index - last_voice > max_gap_windows:
                ranges.append((start, last_voice + 1))
                start = None
                last_voice = None

        if start is not None and last_voice is not None:
            ranges.append((start, last_voice + 1))

        return [
            (start_index * window_sec, end_index * window_sec)
            for start_index, end_index in ranges
        ]

    # {
    #   責務: [rms: サンプル系列の二乗平均平方根を求める]
    #   処理: [1: 空系列を処理し、二乗平均平方根を計算する]
    #   引数: [samples: PCM音声サンプル]
    #   戻り値: [rms: 振幅値の二乗平均平方根]
    # }
    @staticmethod
    def rms(samples: array.array | list[int]) -> float:
        if not samples:
            return 0.0
        return math.sqrt(
            sum(float(value) * float(value) for value in samples) / len(samples)
        )

    # {
    #   責務: [_relative: workspace内の音声パスを相対形式で返す]
    #   処理: [1: workspaceからの相対パスを作り、対象外なら絶対パスを返す]
    #   引数: [path: 出力音声パス, workspace: 相対パスの基準]
    #   戻り値: [relative_path: POSIX形式の相対パスまたは絶対パス]
    # }
    @staticmethod
    def _relative(path: Path, workspace: Path) -> str:
        try:
            return path.resolve().relative_to(workspace.resolve()).as_posix()
        except ValueError:
            return str(path.resolve())
