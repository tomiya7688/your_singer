from __future__ import annotations

import math
from pathlib import Path
from typing import Callable

from .audio_features import _clamp


_whisper_model = None

# {
#   責務: [
#     _transcribe: faster-whisperで日本語文字起こしと単語時刻を得る
#   ]
#   処理: [
#     1: faster-whisperを遅延読み込みする
#     2: 日本語の文字起こしと単語境界を取得する
#     3: 信頼度と時刻付き単語を返す
#   ]
#   引数: [
#     path: 文字起こし対象の音声ファイル
#   ]
#   戻り値: [
#     transcript: 認識した文字列
#     confidence: 区間単位の認識信頼度
#     alignment_units: 単語文字列と開始・終了時刻の一覧
#   ]
#   エラー: [
#     モデルの初期化または音声認識に失敗した場合に失敗する
#   ]
# }
def _transcribe(path: Path) -> tuple[str, float, list[dict]]:
    global _whisper_model

    try:
        from faster_whisper import WhisperModel
    except ImportError:
        return "", 0.0, []

    if _whisper_model is None:
        # CTranslate2のauto選択はCUDA DLLがない環境でもGPUを選ぶため、PyTorchの実利用可否で決める。
        import torch

        device = "cuda" if torch.cuda.is_available() else "cpu"
        _whisper_model = WhisperModel(
            "small",
            device=device,
            compute_type="auto",
        )

    segments, _ = _whisper_model.transcribe(
        str(path),
        language="ja",
        vad_filter=False,
        beam_size=5,
        word_timestamps=True,
    )

    texts: list[str] = []
    logprobs: list[float] = []
    alignment_units: list[dict] = []

    # 認識区間を結合し、単語時刻と区間信頼度をそれぞれ蓄積する。
    for segment in segments:
        text = (segment.text or "").strip()
        if text:
            texts.append(text)
        for word in getattr(segment, "words", None) or []:
            word_text = (getattr(word, "word", "") or "").strip()
            start = getattr(word, "start", None)
            end = getattr(word, "end", None)
            if word_text and start is not None and end is not None and float(end) > float(start):
                alignment_units.append({
                    "text": word_text,
                    "start_sec": float(start),
                    "end_sec": float(end),
                    "confidence": _clamp(float(getattr(word, "probability", 0.0) or 0.0)),
                })
        avg_logprob = getattr(segment, "avg_logprob", None)
        if avg_logprob is not None:
            logprobs.append(float(avg_logprob))

    transcript = "".join(texts).strip()

    # log probabilityがないモデル出力は、中程度の仮値として区別して返す。
    if not logprobs:
        return transcript, 0.5 if transcript else 0.0, alignment_units

    mean_logprob = sum(logprobs) / len(logprobs)
    confidence = 1.0 / (1.0 + math.exp(-(mean_logprob + 1.0) * 2.0))
    return transcript, _clamp(confidence), alignment_units


# {
#   責務: [
#     _phonemize: 日本語文字列をpyopenjtalkで音素列へ変換する
#   ]
#   処理: [
#     1: 空文字列を空の音素列として扱う
#     2: pyopenjtalkのG2P結果を分割して返す
#   ]
#   引数: [
#     text: 変換する日本語文字列
#   ]
#   戻り値: [
#     phonemes: 空白区切りの音素列
#   ]
#   エラー: [
#     pyopenjtalkの読み込みまたは変換に失敗した場合に失敗する
#   ]
# }
def _phonemize(text: str) -> list[str]:
    if not text:
        return []

    try:
        import pyopenjtalk
        phones = pyopenjtalk.g2p(text, kana=False)
        # pyopenjtalkの空白区切り出力を内部音素列へ変換する。
        return [phone for phone in phones.split() if phone]
    except Exception as exc:
        raise RuntimeError("pyopenjtalkによる日本語音素変換に失敗しました。") from exc


# {
#   責務: [
#     align_phonemes: ASR単語時刻を検証し音素時刻の推定値を作る
#   ]
#   処理: [
#     1: 入力音素と音声長を検証する
#     2: ASR単語を音素化し元の音素列との一致を確認する
#     3: 一致した単語区間内で音素時刻を按分する
#   ]
#   引数: [
#     phonemes: transcript全体から得た音素列
#     duration_sec: 音声区間の長さ（秒）
#     asr_confidence: 文字起こしの信頼度
#     alignment_units: ASR単語と単語時刻の一覧
#     phonemize: 音素変換関数
#   ]
#   戻り値: [
#     timings: 単語境界内で按分した音素時刻
#     confidence: 音素時刻の平均信頼度
#     method: 時刻推定方式またはunavailable
#   ]
# }
def align_phonemes(
    phonemes: list[str],
    duration_sec: float,
    asr_confidence: float,
    alignment_units: list[dict] | None,
    phonemize: Callable[[str], list[str]],
) -> tuple[list[dict], float, str]:
    # 音声長か音素列がない区間には時刻や信頼度を割り当てない。
    if not phonemes or duration_sec <= 0:
        return [], 0.0, "unavailable"

    # 認識単語を個別に音素化し、全体の音素列と一致するか調べる。
    result: list[dict] = []
    valid_units = [unit for unit in alignment_units or []
                   if float(unit.get("end_sec", 0.0)) > float(unit.get("start_sec", 0.0))]
    unit_phones = [(unit, phonemize(str(unit.get("text", "")))) for unit in valid_units]
    unit_phones = [(unit, phones) for unit, phones in unit_phones if phones]
    aligned_phones = [phone for _, phones in unit_phones for phone in phones]

    if not unit_phones or aligned_phones != phonemes:
        # ASR timestamps are only useful when their tokenization agrees with G2P.
        # Do not fabricate a segment-wide uniform alignment on a mismatch.
        return [], 0.0, "unavailable"

    # 一致した単語区間内だけを音素数で按分し、範囲外を音声長へ切り詰める。
    confidences: list[float] = []
    for unit, phones in unit_phones:
        start = max(0.0, float(unit["start_sec"]))
        end = min(duration_sec, float(unit["end_sec"]))
        if end <= start:
            continue
        step = (end - start) / len(phones)
        unit_confidence = _clamp(asr_confidence * float(unit.get("confidence", 0.0)))
        for index, phoneme in enumerate(phones):
            phone_start = start + index * step
            phone_end = end if index == len(phones) - 1 else start + (index + 1) * step
            result.append({
                "phoneme": phoneme,
                "start_sec": phone_start,
                "end_sec": phone_end,
                "confidence": unit_confidence,
            })
            confidences.append(unit_confidence)

    if not result:
        return [], 0.0, "unavailable"
    return result, sum(confidences) / len(confidences), "asr-word-boundary-proportional"
