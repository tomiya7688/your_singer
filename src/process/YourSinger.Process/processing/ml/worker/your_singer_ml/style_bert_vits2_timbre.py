"""歌唱由来の声質参照を、Style-Bert-VITS2 のスタイルとして追加する(経路T1)。

スタイルベクトルは音声全体の話者埋め込み(256次元)で、学習済みモデルの style_vectors.npy に行を足すと
推論時に新しいスタイルとして選べる。歌唱音声は教師データではなく、声質の参照としてだけ使う。
埋め込みの計算(pyannote)は呼び出し側が注入する。ここは入力の検証と、成果物への安全な追記を担当する。
"""

from __future__ import annotations

import hashlib
import json
import os
import tempfile
from pathlib import Path
from typing import Callable, Sequence

import numpy as np

STYLE_DIM = 256


class TimbreStyleError(ValueError):
    """声質スタイルを追加できない入力。理由は日本語で返す。"""


# {
#   責務: [compute_reference_vectors: 参照音声から埋め込みを計算し、使えないものを理由付きで除く]
#   処理: [1: 埋め込み関数を各ファイルへ適用する 2: 次元・NaN・無限大を検査する]
#   戻り値: [(vectors: 使えたベクトルの配列, skipped: [(path, 理由)])]
# }
def compute_reference_vectors(
    wav_paths: Sequence[str], embed: Callable[[str], np.ndarray]
) -> tuple[np.ndarray, list[tuple[str, str]]]:
    vectors: list[np.ndarray] = []
    skipped: list[tuple[str, str]] = []
    for path in wav_paths:
        try:
            vector = np.asarray(embed(path), dtype=np.float64).reshape(-1)
        except Exception as error:  # 埋め込みの失敗は、その音声だけ除く
            skipped.append((path, f"埋め込みを計算できません: {error}"))
            continue
        if vector.shape[0] != STYLE_DIM:
            skipped.append((path, f"次元が {STYLE_DIM} ではありません({vector.shape[0]})"))
        elif not np.isfinite(vector).all():
            skipped.append((path, "NaN または無限大を含みます"))
        else:
            vectors.append(vector)
    return (np.stack(vectors) if vectors else np.empty((0, STYLE_DIM))), skipped


# {
#   責務: [append_reference_style: スタイルベクトルの平均を新しいスタイルとして style_vectors.npy と config.json へ追記する]
#   処理: [
#     1: 入力(名前・ベクトル・既存ファイル)を検査する
#     2: 一時ファイルへ書いてから置き換える(失敗時は元のファイルを変えない)
#     3: 追加したスタイルの番号と、使ったベクトルのhashを返す
#   ]
#   エラー: [名前の重複・入力が空・次元不正・既存ファイルの不整合のとき TimbreStyleError]
# }
def append_reference_style(model_dir: str | Path, style_name: str, vectors: np.ndarray) -> dict:
    root = Path(model_dir)
    styles_path, config_path = root / "style_vectors.npy", root / "config.json"
    if not style_name or not style_name.strip() or "/" in style_name or "\\" in style_name:
        raise TimbreStyleError("スタイル名が不正です。")
    vectors = np.asarray(vectors, dtype=np.float64)
    if vectors.ndim != 2 or vectors.shape[0] == 0 or vectors.shape[1] != STYLE_DIM:
        raise TimbreStyleError(f"参照ベクトルが空、または次元が {STYLE_DIM} ではありません。")
    if not np.isfinite(vectors).all():
        raise TimbreStyleError("参照ベクトルに NaN または無限大があります。")
    if not styles_path.is_file() or not config_path.is_file():
        raise TimbreStyleError("style_vectors.npy または config.json がありません。")
    existing = np.load(styles_path)
    if existing.ndim != 2 or existing.shape[1] != STYLE_DIM:
        raise TimbreStyleError("既存の style_vectors.npy の形が不正です。")
    config = json.loads(config_path.read_text(encoding="utf-8"))
    data = config.get("data", {})
    style2id = dict(data.get("style2id", {}))
    if style_name in style2id:
        raise TimbreStyleError(f"同じ名前のスタイルが既にあります: {style_name}")
    if len(style2id) != existing.shape[0] or sorted(style2id.values()) != list(
        range(existing.shape[0])
    ):
        raise TimbreStyleError(
            "config.json の style2id と style_vectors.npy の行数が一致しません。"
        )

    index = existing.shape[0]
    updated = np.vstack([existing, vectors.mean(axis=0, keepdims=True).astype(existing.dtype)])
    style2id[style_name] = index
    data["style2id"], data["num_styles"] = style2id, index + 1
    config["data"] = data
    _replace_atomically(styles_path, lambda handle: np.save(handle, updated))
    _replace_atomically(
        config_path,
        lambda handle: handle.write(
            json.dumps(config, ensure_ascii=False, indent=2).encode("utf-8")
        ),
    )
    return {
        "style_name": style_name,
        "style_index": index,
        "reference_count": int(vectors.shape[0]),
        "vectors_sha256": hashlib.sha256(np.ascontiguousarray(vectors).tobytes()).hexdigest(),
    }


def _replace_atomically(path: Path, write: Callable) -> None:
    handle, temporary = tempfile.mkstemp(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp")
    try:
        with os.fdopen(handle, "wb") as stream:
            write(stream)
        os.replace(temporary, path)
    except Exception:
        if os.path.exists(temporary):
            os.remove(temporary)
        raise
