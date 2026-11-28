from __future__ import annotations

import os
from importlib.metadata import version
from pathlib import Path

# MLパッケージのimportより前に設定する。実行中のモデル取得や外部送信を要求しない。
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"


def japanese_phonemes(text: str) -> list[str]:
    try:
        import pyopenjtalk
    except ImportError as error:
        raise RuntimeError("同梱の日本語音素化モジュールがありません。") from error
    # 同梱辞書の有無を先に検証し、pyopenjtalkによる自動辞書ダウンロードを避ける。
    dictionary = pyopenjtalk.OPEN_JTALK_DICT_DIR
    if isinstance(dictionary, bytes):
        dictionary = os.fsdecode(dictionary)
    if not Path(dictionary).is_dir():
        raise FileNotFoundError("同梱の日本語辞書がありません。")
    return pyopenjtalk.g2p(text, kana=False).split()


# {
#   責務: [QwenBackend: Qwen3-TTSを読み込み補完音声を生成する(VRAM不足時はCPUオフロードへ落とす)]
#   フィールド: [torch: torchモジュール, model: 読み込んだモデル]
# }
class QwenBackend:
    def __init__(self, model_directory: Path, device: str):
        try:
            import torch
            from qwen_tts import Qwen3TTSModel
        except ImportError as error:
            raise RuntimeError("補完ワーカーの依存モジュールが不足しています。配布構成を確認してください。") from error
        if version("qwen-tts") != "0.1.1":
            raise RuntimeError("補完ワーカーのqwen-ttsが対応版と一致しません。")
        if device == "auto":
            device = "cuda:0" if torch.cuda.is_available() else "cpu"
        if device == "cuda:0" and not torch.cuda.is_available():
            raise RuntimeError("指定されたGPUを利用できません。")
        self.torch = torch
        dtype = torch.float32 if device == "cpu" else (
            torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16)
        options = dict(dtype=dtype, attn_implementation="eager", local_files_only=True, use_safetensors=True)
        try:
            self.model = Qwen3TTSModel.from_pretrained(str(model_directory), device_map=device, **options)
        except torch.cuda.OutOfMemoryError:
            # VRAM不足: accelerateのdevice_map="auto"で一部層をCPUへオフロードして再試行する。
            if device == "cpu":
                raise
            torch.cuda.empty_cache()
            self.model = Qwen3TTSModel.from_pretrained(
                str(model_directory), device_map="auto",
                max_memory={0: "6GiB", "cpu": "16GiB"}, **options)

    def synthesize(self, text: str, reference: Path, reference_text: str,
                   seed: int) -> tuple[list[float], int]:
        self.torch.manual_seed(seed)
        with self.torch.inference_mode():
            wavs, rate = self.model.generate_voice_clone(
                text=text, language="Japanese", ref_audio=str(reference), ref_text=reference_text,
                x_vector_only_mode=False, max_new_tokens=250,
            )
        if len(wavs) != 1 or getattr(wavs[0], "ndim", None) != 1:
            raise ValueError("補完モデルから想定外の音声が返されました。")
        return wavs[0].tolist(), int(rate)
