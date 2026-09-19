from __future__ import annotations

import math
import wave
from array import array
from pathlib import Path


def classify_content(payload: dict) -> dict:
    stage_version = str(payload["stage_version"])
    segments = list(payload.get("segments") or [])

    classifications = [
        _classify_segment(
            str(item["segment_id"]),
            Path(item["audio_path"]),
        )
        for item in segments
    ]

    return {
        "stage_version": stage_version,
        "classifications": classifications,
        "overrides": [],
    }


def _classify_segment(segment_id: str, path: Path) -> dict:
    sample_rate, samples = _load_pcm16_mono(path)

    if not samples:
        return _result(segment_id, "ambiguous", 0.0, 0.5, 0.5)

    frame_size = max(1, int(sample_rate * 0.040))
    hop_size = max(1, int(sample_rate * 0.020))

    voiced_frames = 0
    pitch_values: list[float] = []
    energy_values: list[float] = []
    zero_crossing_values: list[float] = []

    for start in range(0, max(1, len(samples) - frame_size + 1), hop_size):
        frame = samples[start:start + frame_size]
        if len(frame) < frame_size:
            break

        rms = _rms(frame)
        energy_values.append(rms)

        if rms < 300.0:
            continue

        voiced_frames += 1
        pitch = _estimate_pitch(frame, sample_rate)
        if pitch is not None:
            pitch_values.append(pitch)

        zero_crossing_values.append(_zero_crossing_rate(frame))

    total_frames = max(1, len(energy_values))
    voiced_ratio = voiced_frames / total_frames

    pitch_ratio = len(pitch_values) / max(1, voiced_frames)
    pitch_stability = _pitch_stability(pitch_values)
    sustained_voicing = _sustained_voicing_score(pitch_values)
    zcr = sum(zero_crossing_values) / max(1, len(zero_crossing_values))
    dynamic_range = _dynamic_range_score(energy_values)

    singing_score = _clamp(
        0.32 * pitch_ratio
        + 0.30 * pitch_stability
        + 0.22 * sustained_voicing
        + 0.10 * voiced_ratio
        + 0.06 * (1.0 - dynamic_range)
    )

    speech_score = _clamp(
        0.28 * voiced_ratio
        + 0.28 * (1.0 - pitch_stability)
        + 0.20 * dynamic_range
        + 0.14 * _clamp(zcr * 8.0)
        + 0.10 * (1.0 - sustained_voicing)
    )

    difference = singing_score - speech_score

    if max(singing_score, speech_score) < 0.52 or abs(difference) < 0.12:
        content_type = "ambiguous"
        confidence = _clamp(abs(difference) * 2.0)
    elif difference > 0:
        content_type = "singing"
        confidence = _clamp(0.5 + abs(difference) * 0.8)
    else:
        content_type = "speech"
        confidence = _clamp(0.5 + abs(difference) * 0.8)

    return _result(
        segment_id,
        content_type,
        confidence,
        speech_score,
        singing_score,
    )


def _load_pcm16_mono(path: Path) -> tuple[int, array]:
    with wave.open(str(path), "rb") as wav:
        if wav.getsampwidth() != 2:
            raise ValueError(f"PCM16 WAVのみ分類できます: {path}")

        sample_rate = wav.getframerate()
        channels = wav.getnchannels()
        samples = array("h", wav.readframes(wav.getnframes()))

    if channels == 1:
        return sample_rate, samples

    mono = array("h")
    for index in range(0, len(samples), channels):
        mono.append(int(sum(samples[index:index + channels]) / channels))

    return sample_rate, mono


def _estimate_pitch(frame: array, sample_rate: int) -> float | None:
    min_hz = 70.0
    max_hz = 1000.0

    min_lag = max(1, int(sample_rate / max_hz))
    max_lag = min(len(frame) - 2, int(sample_rate / min_hz))
    if max_lag <= min_lag:
        return None

    mean = sum(frame) / len(frame)
    centered = [float(value) - mean for value in frame]
    energy = sum(value * value for value in centered)
    if energy <= 1e-9:
        return None

    best_lag = 0
    best_corr = 0.0

    for lag in range(min_lag, max_lag + 1):
        numerator = 0.0
        denominator_a = 0.0
        denominator_b = 0.0

        upper = len(centered) - lag
        for index in range(upper):
            a = centered[index]
            b = centered[index + lag]
            numerator += a * b
            denominator_a += a * a
            denominator_b += b * b

        denominator = math.sqrt(denominator_a * denominator_b)
        if denominator <= 1e-9:
            continue

        correlation = numerator / denominator
        if correlation > best_corr:
            best_corr = correlation
            best_lag = lag

    if best_lag == 0 or best_corr < 0.55:
        return None

    return sample_rate / best_lag


def _pitch_stability(values: list[float]) -> float:
    if len(values) < 3:
        return 0.0

    cents = [
        1200.0 * math.log2(value / 440.0)
        for value in values
        if value > 0
    ]
    if len(cents) < 3:
        return 0.0

    mean = sum(cents) / len(cents)
    variance = sum((value - mean) ** 2 for value in cents) / len(cents)
    stddev = math.sqrt(variance)

    return _clamp(1.0 - stddev / 220.0)


def _sustained_voicing_score(values: list[float]) -> float:
    if len(values) < 2:
        return 0.0

    stable_pairs = 0
    for previous, current in zip(values, values[1:]):
        if previous <= 0 or current <= 0:
            continue
        cents = abs(1200.0 * math.log2(current / previous))
        if cents <= 80.0:
            stable_pairs += 1

    return stable_pairs / max(1, len(values) - 1)


def _zero_crossing_rate(frame: array) -> float:
    if len(frame) < 2:
        return 0.0

    crossings = 0
    previous = frame[0]
    for current in frame[1:]:
        if (previous < 0 <= current) or (previous >= 0 > current):
            crossings += 1
        previous = current

    return crossings / (len(frame) - 1)


def _dynamic_range_score(values: list[float]) -> float:
    if len(values) < 4:
        return 0.0

    ordered = sorted(values)
    low = ordered[max(0, int(len(ordered) * 0.20) - 1)]
    high = ordered[min(len(ordered) - 1, int(len(ordered) * 0.80))]

    if high <= 1e-9:
        return 0.0

    return _clamp((high - low) / high)


def _rms(samples: array) -> float:
    if not samples:
        return 0.0

    return math.sqrt(
        sum(float(value) * float(value) for value in samples) / len(samples)
    )


def _result(
    segment_id: str,
    content_type: str,
    confidence: float,
    speech_score: float,
    singing_score: float,
) -> dict:
    return {
        "segment_id": segment_id,
        "content_type": content_type,
        "confidence": _clamp(confidence),
        "speech_score": _clamp(speech_score),
        "singing_score": _clamp(singing_score),
        "user_overridden": False,
    }


def _clamp(value: float) -> float:
    return max(0.0, min(1.0, float(value)))
