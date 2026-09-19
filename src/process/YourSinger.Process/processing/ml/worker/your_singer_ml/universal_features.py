from __future__ import annotations

import array
import json
import math
import wave
from pathlib import Path


_whisper_model = None


def extract_universal_features(payload: dict) -> dict:
    segment_id = str(payload["segment_id"])
    source_id = str(payload["source_id"])
    audio_path = Path(payload["audio_path"]).resolve()
    workspace = Path(payload["workspace_path"]).resolve()
    cache_key = str(payload["cache_key"])

    sample_rate, samples = _load_pcm16_mono(audio_path)
    duration_sec = len(samples) / max(1, sample_rate)

    transcript, asr_confidence = _transcribe(audio_path)
    phonemes = _phonemize(transcript)
    phoneme_timings, alignment_confidence = _align_phonemes(
        phonemes,
        duration_sec,
        asr_confidence,
    )

    f0_values, energy_values, voicing_values = _extract_frame_features(
        samples,
        sample_rate,
    )

    f0_path = _write_feature(
        workspace / "features" / "f0" / f"{segment_id}.json",
        f0_values,
        workspace,
    )
    energy_path = _write_feature(
        workspace / "features" / "energy" / f"{segment_id}.json",
        energy_values,
        workspace,
    )
    voicing_path = _write_feature(
        workspace / "features" / "voicing" / f"{segment_id}.json",
        voicing_values,
        workspace,
    )

    voiced_f0 = [
        item["value"]
        for item in f0_values
        if item["value"] > 0.0
    ]

    mean_f0 = sum(voiced_f0) / max(1, len(voiced_f0))
    f0_std = _stddev(voiced_f0)
    mean_energy = sum(item["value"] for item in energy_values) / max(1, len(energy_values))
    voiced_ratio = sum(1 for item in voicing_values if item["value"] >= 0.5) / max(
        1, len(voicing_values)
    )
    pitch_reliability = sum(
        item.get("confidence", 0.0)
        for item in f0_values
        if item["value"] > 0.0
    ) / max(1, len(voiced_f0))

    style = {
        "speaking_rate": len(phonemes) / max(duration_sec, 0.001),
        "pitch_range_semitones": _pitch_range_semitones(voiced_f0),
        "energy_variation": _coefficient_of_variation(
            [item["value"] for item in energy_values]
        ),
        "pause_ratio": 1.0 - voiced_ratio,
    }

    return {
        "segment_id": segment_id,
        "source_id": source_id,
        "speaker_id": None,
        "content_type": "ambiguous",
        "audio_path": _relative(audio_path, workspace),
        "cache_key": cache_key,
        "transcript": transcript,
        "asr_confidence": asr_confidence,
        "phonemes": phoneme_timings,
        "alignment_confidence": alignment_confidence,
        "f0_feature_path": f0_path,
        "energy_feature_path": energy_path,
        "voicing_feature_path": voicing_path,
        "pitch_reliability": pitch_reliability,
        "mean_f0_hz": mean_f0,
        "f0_std_dev_hz": f0_std,
        "mean_energy": mean_energy,
        "voiced_ratio": voiced_ratio,
        "style_prosody": style,
        "speaker_embedding": [],
    }


def _transcribe(path: Path) -> tuple[str, float]:
    global _whisper_model

    try:
        from faster_whisper import WhisperModel
    except ImportError:
        return "", 0.0

    if _whisper_model is None:
        _whisper_model = WhisperModel(
            "small",
            device="auto",
            compute_type="auto",
        )

    segments, _ = _whisper_model.transcribe(
        str(path),
        language="ja",
        vad_filter=False,
        beam_size=5,
    )

    texts: list[str] = []
    logprobs: list[float] = []

    for segment in segments:
        text = (segment.text or "").strip()
        if text:
            texts.append(text)
        avg_logprob = getattr(segment, "avg_logprob", None)
        if avg_logprob is not None:
            logprobs.append(float(avg_logprob))

    transcript = "".join(texts).strip()

    if not logprobs:
        return transcript, 0.5 if transcript else 0.0

    mean_logprob = sum(logprobs) / len(logprobs)
    confidence = 1.0 / (1.0 + math.exp(-(mean_logprob + 1.0) * 2.0))
    return transcript, _clamp(confidence)


def _phonemize(text: str) -> list[str]:
    if not text:
        return []

    try:
        import pyopenjtalk
        phones = pyopenjtalk.g2p(text, kana=False)
        return [phone for phone in phones.split() if phone]
    except Exception:
        return [character for character in text if not character.isspace()]


def _align_phonemes(
    phonemes: list[str],
    duration_sec: float,
    asr_confidence: float,
) -> tuple[list[dict], float]:
    if not phonemes or duration_sec <= 0:
        return [], 0.0

    step = duration_sec / len(phonemes)
    result: list[dict] = []

    for index, phoneme in enumerate(phonemes):
        start = index * step
        end = duration_sec if index == len(phonemes) - 1 else (index + 1) * step
        result.append(
            {
                "phoneme": phoneme,
                "start_sec": start,
                "end_sec": end,
                "confidence": _clamp(asr_confidence * 0.8),
            }
        )

    alignment_confidence = _clamp(asr_confidence * 0.8)
    return result, alignment_confidence


def _extract_frame_features(
    samples: array.array,
    sample_rate: int,
) -> tuple[list[dict], list[dict], list[dict]]:
    frame_size = max(1, int(sample_rate * 0.040))
    hop_size = max(1, int(sample_rate * 0.010))

    f0_values: list[dict] = []
    energy_values: list[dict] = []
    voicing_values: list[dict] = []

    for start in range(0, max(1, len(samples) - frame_size + 1), hop_size):
        frame = samples[start:start + frame_size]
        if len(frame) < frame_size:
            break

        time_sec = start / sample_rate
        energy = _rms(frame) / 32768.0
        pitch, confidence = _estimate_pitch(frame, sample_rate)

        f0_values.append(
            {
                "time_sec": time_sec,
                "value": pitch or 0.0,
                "confidence": confidence,
            }
        )
        energy_values.append(
            {
                "time_sec": time_sec,
                "value": energy,
            }
        )
        voicing_values.append(
            {
                "time_sec": time_sec,
                "value": 1.0 if pitch is not None and confidence >= 0.55 else 0.0,
            }
        )

    return f0_values, energy_values, voicing_values


def _estimate_pitch(frame: array.array, sample_rate: int) -> tuple[float | None, float]:
    if _rms(frame) < 250.0:
        return None, 0.0

    min_hz = 60.0
    max_hz = 1200.0
    min_lag = max(1, int(sample_rate / max_hz))
    max_lag = min(len(frame) - 2, int(sample_rate / min_hz))

    mean = sum(frame) / len(frame)
    centered = [float(value) - mean for value in frame]

    best_lag = 0
    best_corr = 0.0

    for lag in range(min_lag, max_lag + 1):
        numerator = 0.0
        a_energy = 0.0
        b_energy = 0.0

        for index in range(len(centered) - lag):
            a = centered[index]
            b = centered[index + lag]
            numerator += a * b
            a_energy += a * a
            b_energy += b * b

        denominator = math.sqrt(a_energy * b_energy)
        if denominator <= 1e-9:
            continue

        correlation = numerator / denominator
        if correlation > best_corr:
            best_corr = correlation
            best_lag = lag

    if best_lag == 0 or best_corr < 0.45:
        return None, _clamp(best_corr)

    return sample_rate / best_lag, _clamp(best_corr)


def _load_pcm16_mono(path: Path) -> tuple[int, array.array]:
    with wave.open(str(path), "rb") as wav:
        if wav.getsampwidth() != 2:
            raise ValueError(f"PCM16 WAVのみ特徴抽出できます: {path}")

        sample_rate = wav.getframerate()
        channels = wav.getnchannels()
        samples = array.array("h", wav.readframes(wav.getnframes()))

    if channels == 1:
        return sample_rate, samples

    mono = array.array("h")
    for index in range(0, len(samples), channels):
        mono.append(int(sum(samples[index:index + channels]) / channels))

    return sample_rate, mono


def _write_feature(path: Path, values: list[dict], workspace: Path) -> str:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(values, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    return _relative(path, workspace)


def _relative(path: Path, workspace: Path) -> str:
    return path.resolve().relative_to(workspace.resolve()).as_posix()


def _pitch_range_semitones(values: list[float]) -> float:
    positive = sorted(value for value in values if value > 0)
    if len(positive) < 2:
        return 0.0

    low = positive[max(0, int(len(positive) * 0.10) - 1)]
    high = positive[min(len(positive) - 1, int(len(positive) * 0.90))]
    if low <= 0 or high <= low:
        return 0.0

    return 12.0 * math.log2(high / low)


def _coefficient_of_variation(values: list[float]) -> float:
    if not values:
        return 0.0

    mean = sum(values) / len(values)
    if mean <= 1e-9:
        return 0.0

    return _stddev(values) / mean


def _stddev(values: list[float]) -> float:
    if len(values) < 2:
        return 0.0

    mean = sum(values) / len(values)
    return math.sqrt(
        sum((value - mean) ** 2 for value in values) / len(values)
    )


def _rms(samples: array.array) -> float:
    if not samples:
        return 0.0
    return math.sqrt(
        sum(float(value) * float(value) for value in samples) / len(samples)
    )


def _clamp(value: float) -> float:
    return max(0.0, min(1.0, float(value)))
