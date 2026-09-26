from __future__ import annotations

import math
import re
import wave
from pathlib import Path

VERSION = "transcript-consensus-1"
MIN_CONFIDENCE = 0.72
MIN_IMPROVEMENT = 0.12


def _confidence(logprob: float) -> float:
    return max(0.0, min(1.0, 1.0 / (1.0 + math.exp(-(logprob + 1.0) * 2.0))))


def _normalize(text: str) -> str:
    return re.sub(r"[\s、。,.!?！？「」『』（）()［］\[\]]+", "", text)


def _phones(text: str) -> list[str]:
    import pyopenjtalk
    values = pyopenjtalk.g2p(text, kana=False).split()
    return [x for x in values if x and x not in {"sil", "pau", "sp", "_"}]


def _decode(model, path: Path, beam_size: int) -> tuple[str, float]:
    segments, _ = model.transcribe(
        str(path),
        language="ja",
        beam_size=beam_size,
        vad_filter=False,
        condition_on_previous_text=False,
    )
    parts = list(segments)
    text = "".join((part.text or "").strip() for part in parts).strip()
    probs = [float(part.avg_logprob) for part in parts if getattr(part, "avg_logprob", None) is not None]
    return text, _confidence(sum(probs) / len(probs)) if probs else (0.5 if text else 0.0)


def review_transcript(payload: dict, model_factory=None) -> dict:
    if not isinstance(payload, dict):
        raise ValueError("文字起こし補正要求が不正です。")
    audio = Path(str(payload.get("audio_path", ""))).resolve(strict=True)
    observed = payload.get("observed_transcript")
    observed_confidence = float(payload.get("observed_confidence", 0.0))
    if not isinstance(observed, str):
        raise ValueError("観測文字起こしが不正です。")
    if not math.isfinite(observed_confidence) or not 0 <= observed_confidence <= 1:
        raise ValueError("観測信頼度が不正です。")

    resources = Path(str(payload.get("resources_root", ""))).resolve()
    asr = resources / "asr"
    if model_factory is None and not asr.is_dir():
        raise FileNotFoundError("文字起こし補正用ASRモデルが同梱されていません。")

    if model_factory is None:
        from faster_whisper import WhisperModel
        model = WhisperModel(str(asr), device="cpu", compute_type="int8", local_files_only=True)
    else:
        model = model_factory()

    text_a, confidence_a = _decode(model, audio, 5)
    text_b, confidence_b = _decode(model, audio, 1)
    phones_a = _phones(text_a) if text_a else []
    phones_b = _phones(text_b) if text_b else []
    confidence = min(confidence_a, confidence_b)

    with wave.open(str(audio), "rb") as reader:
        duration = reader.getnframes() / max(1, reader.getframerate())

    reasons: list[str] = []
    if not text_a or not text_b or not phones_a:
        reasons.append("再認識で有効な文字起こしを取得できませんでした。")
    if phones_a != phones_b:
        reasons.append("再認識2方式の音素列が一致しません。")
    if confidence < MIN_CONFIDENCE:
        reasons.append("再認識の信頼度が採用基準を満たしません。")
    if confidence - observed_confidence < MIN_IMPROVEMENT:
        reasons.append("観測値からの信頼度改善が小さいため自動補正しません。")

    changed = _normalize(text_a) != _normalize(observed)
    if not changed:
        reasons.append("再認識結果が観測文字起こしと同等のため変更しません。")

    return {
        "review_version": VERSION,
        "candidate_transcript": text_a,
        "candidate_phonemes": phones_a,
        "confidence": confidence,
        "duration_sec": duration,
        "changed": changed,
        "machine_passed": not reasons,
        "reasons": reasons,
    }
