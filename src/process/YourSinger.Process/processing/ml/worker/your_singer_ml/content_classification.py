from __future__ import annotations

import array
import math
import wave
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class PitchFrame:
    voiced: bool
    frequency_hz: float
    periodicity: float


def classify_content(payload: dict) -> dict:
    stage_version = str(payload["stage_version"])
    segments = list(payload.get("segments") or [])

    results = []
    for item in segments:
        segment_id = str(item["segment_id"])
        audio_path = Path(item["audio_path"]).resolve()

        speech_confidence, singing_confidence = _classify_file(audio_path)
        difference = abs(singing_confidence - speech_confidence)

        if difference < 0.18 or max(speech_confidence, singing_confidence) < 0.55:
            predicted_type = "ambiguous"
            confidence = 1.0 - difference
        elif singing_confidence > speech_confidence:
            predicted_type = "singing"
            confidence = singing_confidence
        else:
            predicted_type = "speech"
            confidence = speech_confidence

        results.append(
            {
                "segment_id": segment_id,
                "predicted_type": predicted_type,
                "user_override_type": None,
                "speech_confidence": speech_confidence,
                "singing_confidence": singing_confidence,
                "confidence": max(0.0, min(1.0, confidence)),
                "include_in_talk_dataset": predicted_type in ("speech", "ambiguous"),
                "include_in_singing_dataset": predicted_type in ("singing", "ambiguous"),
            }
        )

    return {
        "stage_version": stage_version,
        "segments": results,
    }


def _classify_file(path: Path) -> tuple[float, float]:
    sample_rate, samples = _read_mono_pcm16(path)

    if not samples:
        return 0.5, 0.5

    frames = _pitch_frames(samples, sample_rate)
    voiced = [frame for frame in frames if frame.voiced]

    if not voiced:
        return 0.75, 0.15

    voiced_ratio = len(voiced) / max(1, len(frames))
    frequencies = [frame.frequency_hz for frame in voiced]
    periodicities = [frame.periodicity for frame in voiced]

    median_pitch = _median(frequencies)
    pitch_deviation = _median(
        [abs(1200.0 * math.log2(max(value, 1e-6) / max(median_pitch, 1e-6)))
         for value in frequencies]
    )
    mean_periodicity = sum(periodicities) / len(periodicities)
    sustained_ratio = _sustained_pitch_ratio(voiced)

    # 歌唱は、会話よりも周期性が高く、一定音高を持続する区間が増える傾向を使う。
    singing = (
        0.32 * _clamp((mean_periodicity - 0.35) / 0.55)
        + 0.32 * sustained_ratio
        + 0.20 * _clamp((voiced_ratio - 0.35) / 0.60)
        + 0.16 * _clamp((260.0 - pitch_deviation) / 220.0)
    )

    # 会話は音高変化が大きく、短い有声区間と無声音の交互出現が増えやすい。
    speech = (
        0.36 * _clamp(pitch_deviation / 260.0)
        + 0.26 * _clamp((0.82 - sustained_ratio) / 0.82)
        + 0.22 * _clamp((0.95 - mean_periodicity) / 0.65)
        + 0.16 * _clamp((0.90 - voiced_ratio) / 0.65)
    )

    total = singing + speech
    if total <= 1e-9:
        return 0.5, 0.5

    return _clamp(speech / total), _clamp(singing / total)


def _pitch_frames(samples: array.array, sample_rate: int) -> list[PitchFrame]:
    frame_size = max(1, int(sample_rate * 0.040))
    hop_size = max(1, int(sample_rate * 0.010))

    min_frequency = 70.0
    max_frequency = 900.0
    min_lag = max(1, int(sample_rate / max_frequency))
    max_lag = max(min_lag + 1, int(sample_rate / min_frequency))

    result: list[PitchFrame] = []

    for start in range(0, max(0, len(samples) - frame_size + 1), hop_size):
        frame = samples[start : start + frame_size]
        energy = math.sqrt(
            sum(float(value) * float(value) for value in frame) / max(1, len(frame))
        )

        if energy < 250.0:
            result.append(PitchFrame(False, 0.0, 0.0))
            continue

        best_lag = 0
        best_correlation = 0.0
        zero_lag = sum(float(value) * float(value) for value in frame)

        if zero_lag <= 1e-9:
            result.append(PitchFrame(False, 0.0, 0.0))
            continue

        for lag in range(min_lag, min(max_lag, len(frame) - 1)):
            correlation = 0.0
            for index in range(0, len(frame) - lag):
                correlation += float(frame[index]) * float(frame[index + lag])

            normalized = correlation / zero_lag
            if normalized > best_correlation:
                best_correlation = normalized
                best_lag = lag

        if best_lag == 0 or best_correlation < 0.30:
            result.append(PitchFrame(False, 0.0, best_correlation))
            continue

        result.append(
            PitchFrame(
                True,
                sample_rate / best_lag,
                _clamp(best_correlation),
            )
        )

    return result


def _sustained_pitch_ratio(frames: list[PitchFrame]) -> float:
    if len(frames) < 2:
        return 0.0

    stable = 0
    compared = 0

    for previous, current in zip(frames, frames[1:]):
        if not previous.voiced or not current.voiced:
            continue

        cents = abs(
            1200.0
            * math.log2(
                max(current.frequency_hz, 1e-6)
                / max(previous.frequency_hz, 1e-6)
            )
        )
        compared += 1
        if cents <= 70.0:
            stable += 1

    return stable / max(1, compared)


def _read_mono_pcm16(path: Path) -> tuple[int, array.array]:
    with wave.open(str(path), "rb") as wav:
        if wav.getsampwidth() != 2:
            raise ValueError(f"Speech / Singing分類にはPCM16 WAVが必要です: {path}")

        sample_rate = wav.getframerate()
        channels = wav.getnchannels()
        samples = array.array("h", wav.readframes(wav.getnframes()))

    if channels > 1:
        mono = array.array("h")
        for index in range(0, len(samples), channels):
            mono.append(int(sum(samples[index : index + channels]) / channels))
        samples = mono

    return sample_rate, samples


def _median(values: list[float]) -> float:
    if not values:
        return 0.0

    ordered = sorted(values)
    middle = len(ordered) // 2

    if len(ordered) % 2:
        return ordered[middle]

    return (ordered[middle - 1] + ordered[middle]) / 2.0


def _clamp(value: float) -> float:
    return max(0.0, min(1.0, float(value)))
