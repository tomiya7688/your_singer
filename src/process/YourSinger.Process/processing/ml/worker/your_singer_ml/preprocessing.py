from __future__ import annotations

import array
import json
import math
import shutil
import subprocess
import wave
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


@dataclass(frozen=True)
class WaveStats:
    duration_sec: float
    rms: float
    peak: float
    clipping_ratio: float
    silence_ratio: float
    reverb_score: float


def preprocess_audio(payload: dict) -> dict:
    source_id = str(payload["source_id"])
    input_path = Path(payload["input_path"]).resolve()
    workspace = Path(payload["workspace_path"]).resolve()
    stage_version = str(payload["stage_version"])

    if not input_path.exists():
        raise FileNotFoundError(f"入力音声が見つかりません: {input_path}")

    separated_dir = workspace / "separated"
    cleaned_dir = workspace / "cleaned"
    segments_dir = workspace / "segments"

    separated_dir.mkdir(parents=True, exist_ok=True)
    cleaned_dir.mkdir(parents=True, exist_ok=True)
    segments_dir.mkdir(parents=True, exist_ok=True)

    separated_path = separated_dir / f"{source_id}.wav"
    cleaned_path = cleaned_dir / f"{source_id}.wav"

    history: list[dict] = []

    separation_used = _separate_vocals(input_path, separated_path)
    history.append(_history("separation", "demucs-v4" if separation_used else "passthrough-v1", separated_path, workspace))

    _clean_audio(separated_path, cleaned_path)
    history.append(_history("denoise_normalize", "ffmpeg-afftdn-loudnorm-v1", cleaned_path, workspace))

    stats = _read_wave_stats(cleaned_path)
    segments = _detect_and_export_segments(
        cleaned_path,
        segments_dir,
        source_id,
        workspace,
    )
    history.append(_history("vad_segmentation", "energy-vad-v1", segments_dir, workspace))

    raw_stats = _read_wave_stats(input_path)
    separated_stats = _read_wave_stats(separated_path)

    # BGM漏れはv1-02では完全な知覚モデルではなく、分離前後のRMS差を
    # 品質上の注意値として保存する。後続で専用評価器へ差し替え可能。
    music_leak = _clamp(
        separated_stats.rms / max(raw_stats.rms, 1e-9)
        if separation_used
        else 1.0
    )

    noise_score = _estimate_noise_score(cleaned_path)
    overall = _clamp(
        1.0
        - (
            0.30 * noise_score
            + 0.20 * music_leak
            + 0.20 * stats.reverb_score
            + 0.20 * min(1.0, stats.clipping_ratio * 100.0)
            + 0.10 * stats.silence_ratio
        )
    )

    return {
        "source_id": source_id,
        "stage_version": stage_version,
        "raw_audio_path": _relative(input_path, workspace),
        "separated_audio_path": _relative(separated_path, workspace),
        "cleaned_audio_path": _relative(cleaned_path, workspace),
        "segments": segments,
        "quality": {
            "overall": overall,
            "noise": noise_score,
            "music_leak": music_leak,
            "reverb": stats.reverb_score,
            "clipping": stats.clipping_ratio,
            "silence": stats.silence_ratio,
        },
        "history": history,
        "error_message": None,
    }


def _separate_vocals(input_path: Path, output_path: Path) -> bool:
    try:
        from demucs.separate import main as demucs_main
    except ImportError:
        shutil.copy2(input_path, output_path)
        return False

    temp_root = output_path.parent / f".demucs-{output_path.stem}"
    if temp_root.exists():
        shutil.rmtree(temp_root)
    temp_root.mkdir(parents=True)

    try:
        demucs_main(
            [
                "--two-stems=vocals",
                "--name=htdemucs",
                "--out",
                str(temp_root),
                str(input_path),
            ]
        )

        candidates = list(temp_root.glob("*/**/vocals.wav"))
        if not candidates:
            raise RuntimeError("Demucsのvocals.wavが生成されませんでした。")

        shutil.move(str(candidates[0]), output_path)
        return True
    finally:
        shutil.rmtree(temp_root, ignore_errors=True)


def _clean_audio(input_path: Path, output_path: Path) -> None:
    _run(
        [
            "ffmpeg",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            str(input_path),
            "-af",
            "highpass=f=60,lowpass=f=18000,afftdn=nf=-25,loudnorm=I=-18:LRA=11:TP=-1.5",
            "-ac",
            "1",
            "-ar",
            "48000",
            "-c:a",
            "pcm_s16le",
            str(output_path),
        ]
    )


def _detect_and_export_segments(
    cleaned_path: Path,
    segments_dir: Path,
    source_id: str,
    workspace: Path,
) -> list[dict]:
    with wave.open(str(cleaned_path), "rb") as wav:
        frame_rate = wav.getframerate()
        channels = wav.getnchannels()
        sample_width = wav.getsampwidth()
        if channels != 1 or sample_width != 2:
            raise ValueError("VAD入力はmono PCM16 WAVである必要があります。")

        samples = array.array("h", wav.readframes(wav.getnframes()))

    window_ms = 30
    window = max(1, int(frame_rate * window_ms / 1000))
    min_voice_sec = 0.30
    max_gap_sec = 0.25

    rms_values: list[float] = []
    for start in range(0, len(samples), window):
        block = samples[start : start + window]
        rms_values.append(_rms(block))

    if not rms_values:
        return []

    sorted_rms = sorted(rms_values)
    floor = sorted_rms[max(0, int(len(sorted_rms) * 0.20) - 1)]
    threshold = max(350.0, floor * 3.0)

    voiced = [value >= threshold for value in rms_values]
    ranges = _merge_voiced_windows(voiced, window_ms / 1000.0, max_gap_sec)

    result: list[dict] = []
    segment_index = 0

    for start_sec, end_sec in ranges:
        if end_sec - start_sec < min_voice_sec:
            continue

        segment_id = f"seg_{source_id}_{segment_index:05d}"
        output_path = segments_dir / f"{segment_id}.wav"

        _run(
            [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-ss",
                f"{start_sec:.3f}",
                "-to",
                f"{end_sec:.3f}",
                "-i",
                str(cleaned_path),
                "-c:a",
                "pcm_s16le",
                str(output_path),
            ]
        )

        total_windows = max(1, round((end_sec - start_sec) / (window_ms / 1000.0)))
        result.append(
            {
                "segment_id": segment_id,
                "start_sec": start_sec,
                "end_sec": end_sec,
                "audio_path": _relative(output_path, workspace),
                "voiced_probability": 1.0,
                "silence_ratio": 1.0 / total_windows,
            }
        )
        segment_index += 1

    return result


def _merge_voiced_windows(
    voiced: list[bool],
    window_sec: float,
    max_gap_sec: float,
) -> list[tuple[float, float]]:
    max_gap_windows = max(1, round(max_gap_sec / window_sec))
    ranges: list[tuple[int, int]] = []
    start: int | None = None
    last_voice: int | None = None

    for index, active in enumerate(voiced):
        if active:
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


def _read_wave_stats(path: Path) -> WaveStats:
    with wave.open(str(path), "rb") as wav:
        if wav.getsampwidth() != 2:
            raise ValueError(f"PCM16 WAVのみ品質解析できます: {path}")

        frame_rate = wav.getframerate()
        channels = wav.getnchannels()
        frames = wav.getnframes()
        samples = array.array("h", wav.readframes(frames))

    if channels > 1:
        mono = array.array("h")
        for index in range(0, len(samples), channels):
            mono.append(int(sum(samples[index : index + channels]) / channels))
        samples = mono

    count = max(1, len(samples))
    peak_abs = max((abs(value) for value in samples), default=0)
    clipping = sum(1 for value in samples if abs(value) >= 32700) / count
    silence = sum(1 for value in samples if abs(value) <= 180) / count

    return WaveStats(
        duration_sec=frames / max(1, frame_rate),
        rms=_rms(samples) / 32768.0,
        peak=peak_abs / 32768.0,
        clipping_ratio=clipping,
        silence_ratio=silence,
        reverb_score=_estimate_reverb(samples, frame_rate),
    )


def _estimate_noise_score(path: Path) -> float:
    with wave.open(str(path), "rb") as wav:
        samples = array.array("h", wav.readframes(wav.getnframes()))

    if not samples:
        return 1.0

    block = max(1, len(samples) // 200)
    block_rms = [
        _rms(samples[index : index + block])
        for index in range(0, len(samples), block)
    ]
    block_rms.sort()

    noise_floor = block_rms[max(0, int(len(block_rms) * 0.10) - 1)]
    speech_level = block_rms[max(0, int(len(block_rms) * 0.75) - 1)]

    if speech_level <= 1:
        return 1.0

    ratio = noise_floor / speech_level
    return _clamp(ratio * 3.0)


def _estimate_reverb(samples: array.array, frame_rate: int) -> float:
    if len(samples) < frame_rate // 2:
        return 0.0

    stride = max(1, frame_rate // 200)
    envelope = [
        abs(samples[index])
        for index in range(0, len(samples), stride)
    ]

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

    return _clamp(tail_total / max(1, len(envelope)) * 2.0)


def _rms(samples: array.array | list[int]) -> float:
    if not samples:
        return 0.0
    return math.sqrt(sum(float(value) * float(value) for value in samples) / len(samples))


def _run(command: list[str]) -> None:
    completed = subprocess.run(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        check=False,
    )

    if completed.returncode != 0:
        raise RuntimeError(
            f"外部音声処理に失敗しました: {' '.join(command[:3])}\n{completed.stderr}"
        )


def _relative(path: Path, workspace: Path) -> str:
    try:
        return path.resolve().relative_to(workspace.resolve()).as_posix()
    except ValueError:
        return str(path.resolve())


def _history(stage: str, version: str, output: Path, workspace: Path) -> dict:
    return {
        "stage": stage,
        "version": version,
        "completed_at": datetime.now(timezone.utc).isoformat(),
        "output_path": _relative(output, workspace),
    }


def _clamp(value: float) -> float:
    return max(0.0, min(1.0, float(value)))
