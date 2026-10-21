from __future__ import annotations

import argparse
import importlib.metadata
import json
import os
import sys
from pathlib import Path


# { 責務: [configure_environment: 検証時の一時領域と依存ライブラリのcacheを指定先へまとめる] }
def configure_environment(scratch: Path) -> None:
    for name, relative in {"TEMP": "temp", "TMP": "temp", "HOME": "home", "USERPROFILE": "home",
            "APPDATA": "app-data", "LOCALAPPDATA": "local-app-data", "HF_HOME": "huggingface",
            "TORCH_HOME": "torch", "XDG_CACHE_HOME": "cache", "MPLCONFIGDIR": "matplotlib",
            "NUMBA_CACHE_DIR": "numba", "PYTHONPYCACHEPREFIX": "bytecode"}.items():
        directory = scratch / relative
        directory.mkdir(parents=True, exist_ok=True)
        os.environ[name] = str(directory)
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1", MPLBACKEND="Agg")
    sys.pycache_prefix = str(scratch / "bytecode")


# {
#   責務: [probe: 移動後の専用Pythonから固定モデルと必要なAPIを実際に読み込む]
#   処理: [1: 実行環境を照合する 2: tokenizerと話者モデルを読む 3: GPUがあれば実演算を行う]
#   エラー: [版・依存・モデルの不足や数値演算の不一致で失敗する]
# }
def probe(root: Path) -> dict:
    import numpy
    import pyopenjtalk
    import torch
    import torchaudio
    from pyannote.audio import Model
    from transformers import AutoModelForMaskedLM, AutoTokenizer

    if Path(sys.prefix).resolve() != root / "runtime":
        raise RuntimeError("検証対象bundle内の専用Pythonから起動してください。")
    expected = {"torch": "2.3.1+cu121", "torchaudio": "2.3.1+cu121", "numpy": "1.26.4", "pyopenjtalk-dict": "0.3.4.dev2"}
    actual = {name: importlib.metadata.version(name) for name in expected}
    if actual != expected or torch.version.cuda != "12.1":
        raise RuntimeError("専用runtimeの実際の依存版が固定値と一致しません。")
    if not callable(pyopenjtalk.unset_user_dict) or not Path(os.fsdecode(pyopenjtalk.OPEN_JTALK_DICT_DIR)).is_dir():
        raise RuntimeError("専用runtimeの辞書APIまたは日本語辞書が不足しています。")
    # 音声や歌詞を使わず、同梱モデルのロードと公式特徴APIの数値出力を確認する。
    tokenizer = AutoTokenizer.from_pretrained(root / "bert/deberta-v2-large-japanese-char-wwm", local_files_only=True)
    if not tokenizer("接続確認です")["input_ids"]:
        raise RuntimeError("同梱tokenizerからトークンが生成されません。")
    # device_mapの実経路でBERTを読み、Accelerateなど実行時だけ必要な依存も確認する。
    bert = AutoModelForMaskedLM.from_pretrained(root / "bert/deberta-v2-large-japanese-char-wwm",
        device_map="cpu", local_files_only=True).eval()
    with torch.inference_mode():
        hidden = bert(**tokenizer("接続確認です", return_tensors="pt"), output_hidden_states=True).hidden_states[-3]
    if hidden.shape[-1] != 1024 or not bool(torch.isfinite(hidden).all()):
        raise RuntimeError("公式日本語BERTの数値出力が不正です。")
    bert_shape = list(hidden.shape)
    del bert, hidden
    checkpoint = root / "model-cache/torch/pyannote/models--pyannote--wespeaker-voxceleb-resnet34-LM/snapshots/837717ddb9ff5507820346191109dc79c958d614/pytorch_model.bin"
    model = Model.from_pretrained(str(checkpoint))
    model.eval()
    with torch.inference_mode():
        vector = model(torch.zeros((1, 1, 16000)))
    if tuple(vector.shape) != (1, 256) or not bool(torch.isfinite(vector).all()):
        raise RuntimeError("公式スタイル話者モデルの数値出力が不正です。")
    has_gpu = torch.cuda.is_available()
    if has_gpu:
        matrix = torch.tensor([[1., 2.], [3., 4.]], device="cuda")
        if (matrix @ matrix).cpu().tolist() != [[7., 10.], [15., 22.]]:
            raise RuntimeError("CUDA行列演算の結果が一致しません。")
    return dict(runtime_ready=True, packages=actual, cuda_version=torch.version.cuda, gpu_available=has_gpu,
        gpu_name=torch.cuda.get_device_name() if has_gpu else None, tokenizer_ready=True,
        style_vector_shape=list(vector.shape), bert_hidden_shape=bert_shape, training_run=False)


# { 責務: [main: 構築済みbundleの実ロード確認を行い、結果を指定先へ保存する] }
def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    root = options.root.resolve(strict=True)
    configure_environment(options.output.resolve().parent)
    report = probe(root)
    options.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
