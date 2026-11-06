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
#   責務: [allow_unobserved_special_phonemes: 公式の音素カバレッジ検査のうち、構造用の AP・SP が学習データに現れないことだけを許す]
#   処理: [公式の check_coverage が不足で失敗したとき、不足が AP・SP だけなら続行し、他の音素が不足なら元の例外を再送出する]
#   補足: [
#     公式は辞書に AP・SP を常に加え、全音素が学習データに現れることを要求する。本アプリの音響入力は未測定区間へ AP・SP を付けない
#     (推測を実測として通さない方針)ため、そのままでは全jobが失敗する。この緩和は AP・SP に限り、他の音素の不足は従来どおり失敗にする。
#   ]
# }
def allow_unobserved_special_phonemes() -> None:
    import basics.base_binarizer as base

    original = base.BaseBinarizer.check_coverage

    def check_coverage(self):
        try:
            original(self)
        except base.BinarizationError:
            occurred = {index for item in self.items.values() for index in item["ph_seq"]}
            required = set(range(1, len(self.phoneme_dictionary)))
            missing = {
                self.phoneme_dictionary.decode_one(index, scalar=False)
                for index in required - occurred
            }
            if not missing <= {"AP", "SP"}:
                raise
            print("| AP/SP are not in transcriptions: allowed (structural tokens)")

    base.BaseBinarizer.check_coverage = check_coverage


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
    # 起動スクリプトのフォルダ(your_singer_ml)が先頭にあると、同名の preprocessing.py などが公式のパッケージを隠すため外す。
    script_directory = str(Path(__file__).resolve().parent)
    sys.path[:] = [p for p in sys.path if str(Path(p or ".").resolve()) != script_directory]
    sys.path.insert(0, str(source))
    sampler = load_sampler_module()
    import utils.indexed_datasets as indexed_datasets

    sampler.install_name_recorder(indexed_datasets)
    allow_unobserved_special_phonemes()
    sys.argv = [str(script), *sys.argv[2:]]
    runpy.run_path(str(script), run_name="__main__")


if __name__ == "__main__":
    main()
