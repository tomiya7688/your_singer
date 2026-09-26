"""話者モデルを用いた補完候補生成。学習への採用は別の明示操作で行う。"""
from __future__ import annotations

import array
import hashlib
import json
import math
import os
import sys
import wave
from pathlib import Path

VERSION = "speaker-phoneme-candidate-2"
ASSIST_THRESHOLD = 3
MIN_SIMILARITY = 0.80  # 未校正の採用基準。本人である確率ではない。
MIN_LOGPROB = -0.50
MAX_NO_SPEECH = 0.20


def file_hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def wave_facts(path: Path) -> dict:
    with wave.open(str(path), "rb") as reader:
        if reader.getnchannels() != 1 or reader.getsampwidth() != 2 or reader.getcomptype() != "NONE":
            raise ValueError("補完の検証にはモノラルPCM16 WAVが必要です。")
        rate, count = reader.getframerate(), reader.getnframes()
        if not 16000 <= rate <= 96000 or not 2 <= count / rate <= 14:
            raise ValueError("音声は16～96 kHz、2～14秒の範囲にしてください。")
        data = array.array("h", reader.readframes(count))
    if sys.byteorder != "little":
        data.byteswap()
    if len(data) != count:
        raise ValueError("音声ファイルが途中で切れています。")
    rms = math.sqrt(sum(float(x) * x for x in data) / count) / 32768
    clipping = sum(abs(x) >= 32760 for x in data) / count
    block = max(1, rate // 50)
    energies = [math.sqrt(sum(float(x) * x for x in data[i:i + block]) / len(data[i:i + block])) / 32768
                for i in range(0, count, block)]
    silence = sum(x < 0.008 for x in energies) / len(energies)
    return {"duration_sec": count / rate, "rms": rms, "clipping_ratio": clipping,
            "silence_ratio": silence}


def cosine(a: list[float], b: list[float]) -> float:
    if not a or len(a) != len(b) or not all(math.isfinite(x) for x in a + b):
        raise ValueError("話者特徴が不正です。")
    aa, bb = sum(x*x for x in a), sum(x*x for x in b)
    if aa <= 1e-12 or bb <= 1e-12:
        raise ValueError("話者特徴が空です。")
    return max(-1.0, min(1.0, sum(x*y for x, y in zip(a, b)) / math.sqrt(aa*bb)))


def _phones(values) -> list[str]:
    if not isinstance(values, list) or not values or len(values) > 512 or any(
            not isinstance(x, str) or not x.strip() for x in values):
        raise ValueError("音素列を取得できませんでした。文字列への代替処理は行いません。")
    return [("i" if x == "I" else "u" if x == "U" else x)
            for x in values if x not in {"sil", "pau", "sp", "_"}]


def generate_phoneme_candidate(payload: dict, backend_factory=None) -> dict:
    """backend_factoryはテスト差し替え専用。JSONから実装名や任意コードは受け取らない。"""
    if not isinstance(payload, dict):
        raise ValueError("補完要求が不正です。")
    text = payload.get("text")
    if not isinstance(text, str) or not text.strip() or len(text) > 120 or any(c in text for c in "\r\n|"):
        raise ValueError("補完する文章は改行を含まない120文字以内にしてください。")
    speaker = payload.get("model_speaker")
    if not isinstance(speaker, str) or not speaker.strip():
        raise ValueError("生成モデルの話者名を選択してください。")
    counts = payload.get("observed_phoneme_counts")
    if not isinstance(counts, dict) or any(
            not isinstance(phone, str) or not phone.strip() or
            not isinstance(count, int) or isinstance(count, bool) or count < 0 or count > 1_000_000
            for phone, count in counts.items()):
        raise ValueError("観測音素の回数が不正です。")
    threshold = payload.get("assist_threshold")
    if threshold != ASSIST_THRESHOLD:
        raise ValueError("少量音素の補助閾値がこの生成器の版と一致しません。")
    if backend_factory is None:
        from .runtime_preflight import check_phoneme_supplement_runtime
        runtime = check_phoneme_supplement_runtime({
            "resources_root": payload.get("resources_root", ""),
            "full_verify": False,
        })
        if not runtime["ready"]:
            raise RuntimeError(
                "音素補完runtimeが未準備です。"
                + " / ".join(runtime["issues"])
            )

    reference = Path(payload["reference_audio_path"]).resolve(strict=True)
    output = Path(payload["output_path"]).absolute()
    if output.exists() or output.is_symlink() or not output.parent.is_dir():
        raise ValueError("補完候補の保存先は既存ファイルと分離してください。")
    reference_hash = file_hash(reference)
    facts = wave_facts(reference)
    if facts["rms"] < 0.008 or facts["clipping_ratio"] > 0.001 or facts["silence_ratio"] > 0.60:
        raise ValueError("参照音声の音量・無音率・音割れを確認してください。")
    backend = (backend_factory or LocalModels)(payload)
    expected = _phones(backend.phonemize(text))
    assisted = sorted({phone for phone in expected if counts.get(phone, 0) < threshold})
    missing = sorted(phone for phone in assisted if counts.get(phone, 0) == 0)
    sparse = sorted(phone for phone in assisted if 0 < counts.get(phone, 0) < threshold)
    if not assisted:
        raise ValueError("指定文章には、この話者で未観測または出現量が少ない音素がありません。")
    created = False
    try:
        rate, samples = backend.synthesize(text, speaker)
        # 型を固定し、振幅の自動正規化で音割れを隠さない。
        if rate != 48000 or not isinstance(samples, array.array) or samples.typecode != "h":
            raise ValueError("生成器は48 kHz・PCM16の音声を返す必要があります。")
        if not 2 <= len(samples) / rate <= 14:
            raise ValueError("生成音声の長さが採用範囲外です。")
        with output.open("xb") as stream:
            created = True
            with wave.open(stream, "wb") as writer:
                writer.setnchannels(1); writer.setsampwidth(2); writer.setframerate(rate)
                data = array.array("h", samples)
                if sys.byteorder != "little":
                    data.byteswap()
                writer.writeframes(data.tobytes())
        facts = wave_facts(output)
        recognized, avg_logprob, no_speech = backend.recognize(output)
        actual = _phones(backend.phonemize(recognized)) if recognized.strip() else []
        similarity = cosine(backend.embedding(output), backend.embedding(reference))
        if not all(math.isfinite(x) for x in [avg_logprob, no_speech, similarity]):
            raise ValueError("補完検証の数値が不正です。")
        if file_hash(reference) != reference_hash:
            raise ValueError("処理中に参照音声が変更されました。")
        backend.verify_unchanged()
        reasons = []
        if expected != actual:
            reasons.append("再認識した音素列が指定文章と一致しません。")
        if avg_logprob < MIN_LOGPROB or no_speech > MAX_NO_SPEECH:
            reasons.append("再認識結果の信頼指標が基準を満たしません。")
        if similarity < MIN_SIMILARITY:
            reasons.append("参照音声との話者類似度が基準を満たしません。")
        if facts["rms"] < 0.008 or facts["clipping_ratio"] > 0.001 or facts["silence_ratio"] > 0.60:
            reasons.append("音量・無音率・音割れの基準を満たしません。")
        return {"generator_version": VERSION, "model_fingerprint": backend.fingerprint,
                "reference_sha256": reference_hash, "audio_sha256": file_hash(output),
                "recognized_text": recognized, "expected_phonemes": expected,
                "recognized_phonemes": actual, "missing_phonemes": missing,
                "sparse_phonemes": sparse, "assisted_phonemes": assisted,
                "speaker_similarity": similarity, "asr_avg_logprob": avg_logprob,
                "no_speech_probability": no_speech, "machine_passed": not reasons,
                "reasons": reasons, **facts}
    except BaseException:
        if created and output.exists():
            output.unlink()
        raise


class LocalModels:
    """同梱またはローカル配置済みの信頼できるモデルのみを使用する。"""
    def __init__(self, payload: dict):
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        model = Path(payload["model_directory"]).resolve(strict=True)
        resources = Path(payload["resources_root"]).resolve(strict=True)
        self.paths = [model, resources / "japanese-bert", resources / "asr", resources / "speaker"]
        if not all(p.is_dir() for p in self.paths):
            raise FileNotFoundError("生成・再認識・話者検証モデルが未配置です。補完を保留します。")
        self._manifest = self._hash_models()
        self.fingerprint = hashlib.sha256(json.dumps(self._manifest, sort_keys=True).encode()).hexdigest()
        weights = list(model.glob("*.safetensors"))
        if len(weights) != 1:
            raise ValueError("生成モデルのフォルダには使用するsafetensorsを1件だけ配置してください。")
        import importlib.metadata
        if importlib.metadata.version("style-bert-vits2") != "2.7.0":
            raise RuntimeError("この補完生成器はStyle-Bert-VITS2 2.7.0を対象にしています。")
        import numpy as np
        import torch
        import pyopenjtalk
        from faster_whisper import WhisperModel
        from speechbrain.inference.speaker import EncoderClassifier
        from speechbrain.utils.fetching import FetchConfig, LocalStrategy
        from style_bert_vits2.constants import Languages
        from style_bert_vits2.nlp import bert_models
        from style_bert_vits2.tts_model import TTSModel
        dictionary = os.fsdecode(pyopenjtalk.OPEN_JTALK_DICT_DIR)
        if not Path(dictionary).is_dir():
            raise FileNotFoundError("日本語発音辞書が同梱されていません。")
        self.np, self.torch, self.g2p = np, torch, pyopenjtalk.g2p
        bert_models.load_model(Languages.JP, str(self.paths[1]))
        bert_models.load_tokenizer(Languages.JP, str(self.paths[1]))
        # CPUでの検証可能性を優先。乱数シードは記録対象の固定値とする。
        torch.manual_seed(0)
        self.tts = TTSModel(model_path=weights[0], config_path=model / "config.json",
                            style_vec_path=model / "style_vectors.npy", device="cpu")
        self.asr = WhisperModel(str(self.paths[2]), device="cpu", compute_type="int8", local_files_only=True)
        self.encoder = EncoderClassifier.from_hparams(
            source=str(self.paths[3]), savedir=str(self.paths[3]),
            overrides={"pretrained_path": str(self.paths[3])},
            run_opts={"device": "cpu"}, local_strategy=LocalStrategy.NO_LINK,
            fetch_config=FetchConfig(allow_network=False))

    def _hash_models(self) -> list:
        result = []
        for index, root in enumerate(self.paths):
            if not root.is_dir():
                raise FileNotFoundError("補完モデルが未配置です。")
            files = sorted(root.rglob("*"))
            if any(p.is_symlink() for p in files):
                raise ValueError("補完モデルはリンクではなく実ファイルで配置してください。")
            result.extend((index, p.relative_to(root).as_posix(), file_hash(p)) for p in files if p.is_file())
        return result

    def verify_unchanged(self):
        if self._manifest != self._hash_models():
            raise ValueError("生成・検証中にモデルが変更されました。")

    def phonemize(self, text: str) -> list[str]:
        return self.g2p(text, kana=False).split()

    def synthesize(self, text: str, speaker: str):
        if speaker not in self.tts.spk2id:
            raise ValueError("指定した話者が生成モデルにありません。")
        rate, audio = self.tts.infer(text=text, speaker_id=self.tts.spk2id[speaker],
                                   language="JP", style="Neutral", line_split=False)
        np = self.np
        if audio.ndim != 1 or audio.dtype != np.int16:
            raise ValueError("生成モデルの音声形式が想定と異なります。")
        from scipy.signal import resample_poly
        divisor = math.gcd(int(rate), 48000)
        floats = resample_poly(audio.astype(np.float64) / 32768, 48000 // divisor, int(rate) // divisor)
        if not np.isfinite(floats).all() or np.max(np.abs(floats)) >= 1:
            raise ValueError("生成音声に不正値または音割れがあります。")
        return 48000, array.array("h", np.round(floats * 32767).astype(np.int16).tolist())

    def recognize(self, path: Path):
        parts, _ = self.asr.transcribe(str(path), language="ja", beam_size=5, vad_filter=False,
                                      condition_on_previous_text=False)
        parts = list(parts)
        if not parts:
            return "", -100.0, 1.0
        return "".join(p.text for p in parts).strip(), min(p.avg_logprob for p in parts), max(p.no_speech_prob for p in parts)

    def embedding(self, path: Path) -> list[float]:
        import soundfile as sf
        from scipy.signal import resample_poly
        audio, rate = sf.read(path, dtype="float32")
        if audio.ndim != 1:
            raise ValueError("話者検証にはモノラル音声が必要です。")
        divisor = math.gcd(int(rate), 16000)
        audio = resample_poly(audio, 16000 // divisor, int(rate) // divisor)
        with self.torch.inference_mode():
            vector = self.encoder.encode_batch(self.torch.from_numpy(audio.copy()).unsqueeze(0))
        return vector.squeeze().cpu().tolist()
