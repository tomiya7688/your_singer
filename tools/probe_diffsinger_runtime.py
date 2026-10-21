from __future__ import annotations

import argparse
import json
import os
import platform
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from prepare_diffsinger_source import verify_source


# {
#   責務: [configure_scratch: Trainerの一時ファイルとPython bytecodeを指定した領域へ固定する]
#   引数: [root: 配布配置先, output: 破棄可能な診断出力領域]
# }
def configure_scratch(root: Path, output: Path) -> None:
    root.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)
    for variable, folder in (("TEMP", "temp"), ("TMP", "temp"), ("HOME", "home"),
                             ("USERPROFILE", "home"), ("APPDATA", "app-data"),
                             ("LOCALAPPDATA", "local-app-data"), ("MPLCONFIGDIR", "matplotlib"),
                             ("NUMBA_CACHE_DIR", "numba"), ("TORCH_HOME", "torch")):
        path = root / "runtime-state" / folder
        path.mkdir(parents=True, exist_ok=True)
        os.environ[variable] = str(path)
    sys.pycache_prefix = str(root / "runtime-state" / "bytecode")
    os.environ["WANDB_MODE"] = "disabled"


# {
#   責務: [probe_runtime: Python依存・CUDA・公式学習入口を読み込んでruntime状態を記録する]
#   引数: [root: 専用runtime, source: 固定DiffSingerソース, output: probe JSON]
#   戻り値: [summary: 検査結果と学習未実行フラグ]
# }
def probe_runtime(root: Path, source: Path, output: Path, lock: dict) -> dict:
    if Path(sys.prefix).resolve() != root.resolve():
        raise RuntimeError("専用runtimeからprobeを実行してください。")
    verify_source(source, lock)
    sys.path.insert(0, str(source))
    import importlib.metadata as metadata
    import h5py
    import librosa
    import numpy as np
    import parselmouth
    import pyworld
    import torch
    import torchaudio
    from training.acoustic_task import AcousticTask

    expected = {"torch": "2.3.1+cu121", "torchaudio": "2.3.1+cu121", "numpy": "1.26.4",
                "lightning": "2.3.3", "praat-parselmouth": "0.4.3", "pyworld": "0.3.4"}
    versions = {name: metadata.version(name) for name in expected}
    for name, version in expected.items():
        if versions[name] != version:
            raise RuntimeError(f"固定依存とruntimeの版が一致しません: {name}")
    if AcousticTask.__name__ != "AcousticTask":
        raise RuntimeError("DiffSinger acoustic Trainer入口を読み込めません。")
    matrix_verified = False
    if torch.cuda.is_available():
        matrix = torch.eye(4, device="cuda") @ (torch.eye(4, device="cuda") * 2.0)
        matrix_verified = bool(torch.equal(matrix.cpu(), torch.eye(4) * 2.0))
        if not matrix_verified:
            raise RuntimeError("CUDA演算結果が一致しません。")
    result = {"python_version": platform.python_version(), "versions": versions,
              "required_imports": ["h5py", "librosa", "parselmouth", "pyworld"],
              "cuda_available": torch.cuda.is_available(), "cuda_matrix_verified": matrix_verified,
              "cuda_version": torch.version.cuda,
              "gpu_name": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,
              "official_training_entry_imported": True, "source_version": lock["version"],
              "source_commit": lock["commit"], "training_run": False,
              "binarization_run": False, "openutau_render_verified": False}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


# {
#   責務: [main: 専用runtimeから固定DiffSinger Trainerのimport確認を実行する]
# }
def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--lock", type=Path, required=True)
    arguments = parser.parse_args()
    root, source, output = (arguments.root.resolve(strict=True), arguments.source.resolve(strict=True),
                            arguments.output.resolve())
    configure_scratch(root, output.parent)
    lock = json.loads(arguments.lock.read_text(encoding="utf-8"))
    print(json.dumps(probe_runtime(root, source, output, lock)))


if __name__ == "__main__":
    main()

