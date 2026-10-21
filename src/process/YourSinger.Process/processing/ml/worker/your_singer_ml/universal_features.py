from __future__ import annotations

from pathlib import Path

from .mfa_alignment import ALIGNMENT_METHOD, MfaForcedAligner
from .audio_features import (
    _clamp,
    _coefficient_of_variation,
    _extract_frame_features,
    _load_pcm16_mono,
    _pitch_range_semitones,
    _relative,
    _stddev,
    _write_feature,
)
from .transcription_alignment import (
    _phonemize,
    _transcribe,
    align_phonemes as _align_phonemes_impl,
)
# {
#   責務: [
#     extract_universal_features: 音声区間から文字起こし・音素・音響特徴量を抽出する
#   ]
#   処理: [
#     1: 入力音声と作業領域を読み込む
#     2: 文字起こしまたは修正歌詞から音素境界を実測する
#     3: 音響特徴量を保存し集約値を返す
#   ]
#   引数: [
#     payload: segment ID、音声パス、作業領域、キャッシュキー、任意の修正歌詞を含む要求
#   ]
#   戻り値: [
#     feature: 文字起こし、音素、特徴量パス、品質値を含む抽出結果
#   ]
#   エラー: [
#     音声の読み込みまたは特徴量抽出に失敗した場合に失敗する
#   ]
# }
def extract_universal_features(payload: dict) -> dict:
    segment_id = str(payload["segment_id"])
    source_id = str(payload["source_id"])
    audio_path = Path(payload["audio_path"]).resolve()
    workspace = Path(payload["workspace_path"]).resolve()
    cache_key = str(payload["cache_key"])

    sample_rate, samples = _load_pcm16_mono(audio_path)
    duration_sec = len(samples) / max(1, sample_rate)

    transcript_override = payload.get("transcript_override")
    if transcript_override is None:
        transcript, asr_confidence, alignment_units = _transcribe(audio_path)
    else:
        # 利用者の修正歌詞を再認識結果で上書きせず、その発音列を再実測する。
        if not isinstance(transcript_override, str):
            raise ValueError("文字起こし修正は文字列で指定してください。")
        transcript, asr_confidence, alignment_units = transcript_override, 0.0, []
    phonemes = _phonemize(transcript)
    phoneme_timings, alignment_confidence, alignment_method, alignment_warning = _align_training_phonemes(
        audio_path, workspace, phonemes, duration_sec, asr_confidence, alignment_units)

    # 音声フレームごとのF0、エネルギー、有声判定を抽出する。
    f0_values, energy_values, voicing_values = _extract_frame_features(
        samples,
        sample_rate,
    )

    # 後続工程が個別に読み込めるよう特徴量系列を分けて保存する。
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

    # 音響系列と音素数から区間全体の韻律要約を作る。
    style = {
        "speaking_rate": len(phonemes) / max(duration_sec, 0.001),
        "pitch_range_semitones": _pitch_range_semitones(voiced_f0),
        "energy_variation": _coefficient_of_variation(
            [item["value"] for item in energy_values]
        ),
        "pause_ratio": 1.0 - voiced_ratio,
    }

    # C#側のUniversalVoiceSegment形式に合わせて抽出結果を返す。
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
        "alignment_method": alignment_method,
        "alignment_warning": alignment_warning,
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


# {
#   責務: [_align_training_phonemes: 実測を優先し音素時刻の方式と限界を返す]
#   処理: [1: 同梱MFAで音素境界を実測する 2: 未対応発音や不正出力を推定値と区別する]
#   引数: [audio_path: 音声, workspace: 作業領域, phonemes: 内部音素列, duration_sec: 音声長, asr_confidence: 認識信頼度, alignment_units: ASR単語時刻]
#   戻り値: [alignment: 音素時刻、未校正の信頼度、方式、警告]
#   エラー: [必須資源の欠落やMFA実行失敗は呼出元へ伝える]
# }
def _align_training_phonemes(audio_path: Path, workspace: Path, phonemes: list[str],
                             duration_sec: float, asr_confidence: float,
                             alignment_units: list[dict]) -> tuple[list[dict], float, str, str | None]:
    if not phonemes:
        return [], 0.0, "unavailable", "文字起こしから音素を取得できませんでした。"
    try:
        timings = MfaForcedAligner().align(audio_path, phonemes, duration_sec, workspace)
    except ValueError as error:
        # 推定値には実測方式を付けず、歌唱学習の既存検証で拒否できるようにする。
        timings, confidence, method = _align_phonemes(phonemes, duration_sec, asr_confidence, alignment_units)
        return timings, confidence, method, str(error)
    return timings, 0.0, ALIGNMENT_METHOD, "MFA境界の正確さは未確認です。信頼度は未校正のため0を記録しています。"

# {
#   責務: [
#     _align_phonemes: 互換用音素化関数を注入して音素時刻を推定する
#   ]
#   処理: [1: モジュール内のアラインメント処理へ音素化関数を渡す]
#   引数: [
#     phonemes: 音素列
#     duration_sec: 区間長（秒）
#     asr_confidence: ASR信頼度
#     alignment_units: ASR単語時刻
#   ]
#   戻り値: [alignment: 音素時刻・信頼度・推定方式]
# }
def _align_phonemes(
    phonemes: list[str],
    duration_sec: float,
    asr_confidence: float,
    alignment_units: list[dict] | None = None,
) -> tuple[list[dict], float, str]:
    return _align_phonemes_impl(
        phonemes,
        duration_sec,
        asr_confidence,
        alignment_units,
        _phonemize,
    )
