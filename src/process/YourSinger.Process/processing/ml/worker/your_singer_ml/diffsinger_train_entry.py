from __future__ import annotations

import importlib.util
import os
import runpy
import sys
from pathlib import Path


# {
#   責務: [load_sampler_module: 同じフォルダの重み付きサンプラーを、パッケージ外(専用Python)から読み込む]
# }
def load_sampler_module():
    spec = importlib.util.spec_from_file_location(
        "diffsinger_weighted_sampler", Path(__file__).with_name("diffsinger_weighted_sampler.py")
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# {
#   責務: [main: 固定DiffSingerの学習入口を、重み付きサンプラーに差し替えて起動する(専用Pythonから呼ぶ)]
#   処理: [
#     1: 引数(trainer_root・job_dir・seed)から重みmanifestを検証してitem名→weightを得る
#     2: 公式 basics.base_task の DsBatchSampler を重み付き版へ差し替える(検証用は公式のまま)
#     3: 公式の scripts/train.py を、元の引数のまま実行する
#   ]
#   エラー: [manifestの改竄・欠落・item名の不一致は学習前または最初のバッチ形成で失敗(等重みで続行しない)]
# }
def main() -> None:
    trainer_root, job_dir, seed = (
        Path(sys.argv[1]).resolve(),
        Path(sys.argv[2]).resolve(),
        int(sys.argv[3]),
    )
    separator = sys.argv.index("--")
    train_arguments = sys.argv[separator + 1 :]
    source = trainer_root / "source"
    train_script = source / "scripts" / "train.py"
    # 公式の scripts/train.py と同じく、固定ソースをimport元にする。
    os.environ["PYTHONPATH"] = str(source)
    # 起動スクリプトのフォルダ(your_singer_ml)が先頭にあると、同名の preprocessing.py などが公式のパッケージを隠すため外す。
    script_directory = str(Path(__file__).resolve().parent)
    sys.path[:] = [p for p in sys.path if str(Path(p or ".").resolve()) != script_directory]
    sys.path.insert(0, str(source))
    sampler = load_sampler_module()
    weights, digest = sampler.load_name_weights(job_dir)
    import basics.base_task as base_task

    report = os.environ.get("YOURSINGER_WEIGHT_REPORT") or str(
        job_dir / "trainer" / "weight_sampling.json"
    )
    sampler.install_sampler(base_task, weights, seed, digest, Path(report))
    sys.argv = [str(train_script), *train_arguments]
    runpy.run_path(str(train_script), run_name="__main__")


if __name__ == "__main__":
    main()
