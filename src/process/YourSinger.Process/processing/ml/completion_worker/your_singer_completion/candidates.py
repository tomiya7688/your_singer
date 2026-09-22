from __future__ import annotations

import array
import hashlib
import json
import math
import os
import re
import shutil
import sys
import wave
from pathlib import Path
from typing import Callable, Protocol, Sequence

STAGE_VERSION = "phoneme-candidates-1"
# 固定文から、実際のG2P結果で不足音を含むものを選ぶ。文字を音素とみなさない。
PROMPTS = (
    "青い海を見ながら、ゆっくり歩きます。",
    "今日は学校で、楽しく歌を歌いました。",
    "静かな部屋で、新しい本を読みます。",
    "父と母が、庭の花に水をやります。",
    "午後は友達と、公園で遊びました。",
    "大きな窓から、暖かい風が入ります。",
    "パパが買ったパンを、みんなで食べます。",
    "きっと明日は、もっと良い日になります。",
    "牛乳を飲んでから、病院へ向かいます。",
    "写真を見ながら、旅行の話をしました。",
    "客席から、急に拍手が聞こえました。",
    "人形を持った子が、にゃんこと呼びました。",
    "百円で買った冷たいお茶を飲みます。",
    "三百枚の紙に、名前を書きました。",
    "ぴょんと跳ねたウサギが、ぴゅっと走りました。",
    "脈を測ってから、明日の予定を決めます。",
    "料理の材料を、両手で運びました。",
    "ゼロから図を描いて、全員で見ました。",
    "ファイルを開き、写真を確認します。",
    "ティーカップとディスクを、机に置きました。",
    "ヴァイオリンの音を、静かに聞きます。",
)


class Synthesizer(Protocol):
    def synthesize(self, text: str, reference: Path, reference_text: str,
                   seed: int) -> tuple[Sequence[float], int]: ...


def _text(value: object, label: str, maximum: int = 512) -> str:
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ValueError(f"{label}が不正です。")
    if any(ord(c) < 32 for c in value):
        raise ValueError(f"{label}に制御文字が含まれています。")
    return value


def _integer(value: object, label: str, low: int, high: int) -> int:
    if type(value) is not int or not low <= value <= high:
        raise ValueError(f"{label}が範囲外です。")
    return value


def _hash_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _plain_path(path: Path) -> Path:
    if not path.is_absolute():
        raise ValueError("絶対パスを指定してください。")
    # シンボリックリンク経由で入力・出力の境界を越えない。
    if any(p.is_symlink() for p in (path, *path.parents)):
        raise ValueError("リンク経由のパスは補完候補生成では使用できません。")
    return path.resolve()


def _model_digest(root: Path) -> str:
    if not root.is_dir() or not (root / "config.json").is_file():
        raise FileNotFoundError("同梱の補完用モデルがありません。配布構成を確認してください。")
    files = sorted(root.rglob("*"))
    if any(p.is_symlink() for p in files):
        raise ValueError("モデルはリンクではなく実ファイルで配置してください。")
    weights = [p for p in files if p.suffix == ".safetensors"]
    if not weights:
        raise FileNotFoundError("補完用モデルのsafetensors重みがありません。")
    digest = hashlib.sha256()
    for path in files:
        if path.is_file():
            digest.update(path.relative_to(root).as_posix().encode("utf-8") + b"\0")
            digest.update(_hash_file(path).encode("ascii") + b"\n")
    return digest.hexdigest()


def inspect_reference(path: Path) -> float:
    if path.stat().st_size > 12_000_000:
        raise ValueError("参照音声が大きすぎます。3秒以上15秒以下の区間を選んでください。")
    with wave.open(str(path), "rb") as wav:
        if wav.getnchannels() != 1 or wav.getsampwidth() != 2 or wav.getcomptype() != "NONE":
            raise ValueError("参照音声にはモノラルPCM16 WAVが必要です。")
        rate, count = wav.getframerate(), wav.getnframes()
        if not 16000 <= rate <= 96000 or not 3 <= count / rate <= 15:
            raise ValueError("参照音声は16〜96kHz、3秒以上15秒以下である必要があります。")
        raw = wav.readframes(count)
        if len(raw) != count * 2:
            raise ValueError("参照WAVが途中で切れています。")
    samples = array.array("h", raw)
    if sys.byteorder != "little":
        samples.byteswap()
    rms = math.sqrt(sum((x / 32768.0) ** 2 for x in samples) / len(samples))
    clipping = sum(abs(x) >= 32700 for x in samples) / len(samples)
    if rms < 0.003 or clipping > 0.01:
        raise ValueError("参照音声の音量不足またはクリッピングを検出しました。")
    return count / rate


def choose_prompts(missing: Sequence[str], phonemize: Callable[[str], Sequence[str]],
                   maximum: int) -> tuple[list[dict], list[str]]:
    remaining = set(missing)
    candidates = []
    for index, text in enumerate(PROMPTS):
        phones = list(phonemize(text))
        if not phones or any(not isinstance(p, str) for p in phones):
            raise ValueError("音素化の結果が不正です。文字単位への代用は行いません。")
        # 日本語の無声化母音をカバレッジの母音へ正規化する。
        normalized = {p.lower() if p in ("I", "U") else p for p in phones}
        candidates.append((index, text, phones, normalized))
    result = []
    while remaining and candidates and len(result) < maximum:
        best = min(candidates, key=lambda item: (-len(item[3] & remaining), item[0]))
        targets = sorted(best[3] & remaining)
        if not targets:
            break
        result.append({"text": best[1], "expected_phonemes": best[2], "target_phonemes": targets})
        remaining.difference_update(targets)
        candidates.remove(best)
    return result, sorted(remaining)


def _write_audio(path: Path, values: Sequence[float], rate: int) -> dict:
    _integer(rate, "生成音声のサンプリング周波数", 16000, 96000)
    if not rate <= len(values) <= 20 * rate:
        raise ValueError("生成音声の長さが1〜20秒の範囲外です。")
    floats = [float(x) for x in values]
    if any(not math.isfinite(x) or abs(x) > 1 for x in floats):
        raise ValueError("生成音声に非数値または範囲外の振幅があります。")
    rms = math.sqrt(sum(x * x for x in floats) / len(floats))
    clipping = sum(abs(x) >= 0.999 for x in floats) / len(floats)
    if rms < 0.003 or clipping > 0.01:
        raise ValueError("生成音声が無音に近いか、クリッピングしています。")
    samples = array.array("h", (round(x * 32767) for x in floats))
    if sys.byteorder != "little":
        samples.byteswap()
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(samples.tobytes())
    return {"duration_sec": len(floats) / rate, "sample_rate": rate,
            "rms": rms, "clipping_ratio": clipping, "sha256": _hash_file(path)}


def generate_candidates(payload: dict, *,
                        backend_factory: Callable[[Path, str], Synthesizer] | None = None,
                        phonemize: Callable[[str], Sequence[str]] | None = None) -> dict:
    if not isinstance(payload, dict):
        raise ValueError("補完要求はオブジェクトである必要があります。")
    batch_id = _text(payload.get("batch_id"), "候補ID", 32)
    if not re.fullmatch("[0-9a-f]{32}", batch_id):
        raise ValueError("候補IDの形式が不正です。")
    speaker = _text(payload.get("speaker_id"), "話者ID", 128)
    reference_id = _text(payload.get("reference_segment_id"), "参照区間ID", 128)
    reference_text = _text(payload.get("reference_text"), "参照音声の文章", 500)
    snapshot = _text(payload.get("snapshot_fingerprint"), "入力識別子", 64)
    expected_hash = _text(payload.get("reference_sha256"), "参照音声の識別子", 64)
    if not all(re.fullmatch("[0-9a-f]{64}", x) for x in (snapshot, expected_hash)):
        raise ValueError("入力識別子の形式が不正です。")
    missing = payload.get("missing_phonemes")
    if not isinstance(missing, list) or not 1 <= len(missing) <= 64:
        raise ValueError("不足音素が指定されていません。")
    missing = sorted({_text(p, "不足音素", 8) for p in missing})
    maximum = _integer(payload.get("max_candidates", 4), "生成上限", 1, 8)
    seed = _integer(payload.get("seed", 42), "乱数の種", 0, 2 ** 31 - 1)
    device = payload.get("device", "auto")
    if device not in ("auto", "cpu", "cuda:0"):
        raise ValueError("実行デバイスの指定が不正です。")
    workspace = _plain_path(Path(_text(payload.get("workspace_path"), "プロジェクトパス", 4096)))
    if not workspace.is_dir() or not (workspace / "project.json").is_file():
        raise FileNotFoundError("プロジェクトが見つかりません。")
    reference = _plain_path(Path(_text(payload.get("reference_audio_path"), "参照音声パス", 4096)))
    if not reference.is_relative_to(workspace) or not reference.is_file():
        raise ValueError("参照音声はプロジェクト内の観測音声を指定してください。")
    if _hash_file(reference) != expected_hash:
        raise ValueError("参照音声が計画後に変更されています。")
    inspect_reference(reference)
    model_root = _plain_path(Path(_text(payload.get("model_directory"), "モデルパス", 4096)))
    model_hash = _model_digest(model_root)
    if phonemize is None:
        from .qwen_backend import japanese_phonemes
        phonemize = japanese_phonemes
    prompts, uncovered = choose_prompts(missing, phonemize, maximum)
    if not prompts:
        raise ValueError("不足音素を含む生成文が見つかりません。実録音での追加を検討してください。")
    root = _plain_path(workspace / "cache" / "phoneme-candidates")
    destination = root / batch_id
    staging = root / (".pending-" + batch_id)
    root.mkdir(parents=True, exist_ok=True)
    # 同じ要求IDを再実行しても、既存候補を消したり上書きしたりしない。
    if destination.exists() or staging.exists():
        raise FileExistsError("同じIDの補完候補が存在します。新しい要求で生成してください。")
    staging.mkdir()
    try:
        if backend_factory is None:
            from .qwen_backend import QwenBackend
            backend_factory = QwenBackend
        backend = backend_factory(model_root, device)
        items = []
        for index, prompt in enumerate(prompts):
            values, rate = backend.synthesize(prompt["text"], reference, reference_text, seed + index)
            name = f"candidate-{index + 1:02d}.wav"
            metrics = _write_audio(staging / name, values, rate)
            items.append({**prompt, **metrics, "audio_path": (destination / name).relative_to(workspace).as_posix(),
                          "seed": seed + index, "state": "pending_review", "training_eligible": False,
                          "phoneme_verification": "not_performed", "speaker_verification": "not_performed"})
        # 生成途中に参照音声やモデルが差し替わった結果を公開しない。
        if _hash_file(reference) != expected_hash or _model_digest(model_root) != model_hash:
            raise ValueError("生成中に入力またはモデルが変更されたため候補を破棄しました。")
        manifest = {"schema_version": 1, "stage_version": STAGE_VERSION, "batch_id": batch_id,
                    "speaker_id": speaker, "reference_segment_id": reference_id,
                    "reference_audio_path": reference.relative_to(workspace).as_posix(),
                    "reference_sha256": expected_hash, "reference_text": reference_text,
                    "snapshot_fingerprint": snapshot, "generator": "qwen3-tts-base",
                    "generator_package": "qwen-tts==0.1.1", "model_sha256": model_hash,
                    "requested_device": device, "content_type": "speech", "origin": "synthetic",
                    "training_eligible": False, "missing_phonemes": missing,
                    "unplanned_phonemes": uncovered, "items": items}
        (staging / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, allow_nan=False,
                                                           indent=2), encoding="utf-8")
        os.rename(staging, destination)
        return {"batch_id": batch_id, "manifest_path": (destination / "manifest.json").relative_to(workspace).as_posix(),
                "candidate_count": len(items), "training_eligible": False}
    finally:
        if staging.exists():
            shutil.rmtree(staging)
