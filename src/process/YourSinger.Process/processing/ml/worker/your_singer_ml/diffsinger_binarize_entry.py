from __future__ import annotations

import importlib.util
import os
import runpy
import sys
from pathlib import Path


# { 責務: [load_sampler_module: 同じフォルダの記録用モジュールを、パッケージ外(専用Python)から読み込む] }
def load_sampler_module():
    spec = importlib.util.spec_from_file_location(
        "diffsinger_weighted_sampler", Path(__file__).with_name("diffsinger_weighted_sampler.py")
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# {
#   責務: [main: 固定DiffSingerの公式binarizationを、学習itemの名前を記録する状態で起動する(専用Pythonから呼ぶ)]
#   処理: [
#     1: 固定ソースをimport元にする
#     2: 公式 utils.indexed_datasets の書き込みを、item番号→名前を train.names.json へ残すものへ差し替える
#     3: 公式の scripts/binarize.py を、元の引数のまま実行する
#   ]
#   引数: [argv[1]: trainer_root, argv[2]以降: 公式binarize.pyへ渡す引数(--config など)]
# }
def main() -> None:
    trainer_root = Path(sys.argv[1]).resolve()
    source = trainer_root / "source"
    script = source / "scripts" / "binarize.py"
    os.environ["PYTHONPATH"] = str(source)
    sys.path.insert(0, str(source))
    sampler = load_sampler_module()
    import utils.indexed_datasets as indexed_datasets

    sampler.install_name_recorder(indexed_datasets)
    sys.argv = [str(script), *sys.argv[2:]]
    runpy.run_path(str(script), run_name="__main__")


if __name__ == "__main__":
    main()
