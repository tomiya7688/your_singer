from __future__ import annotations

import json
import os
import runpy
import shutil
import socket
import subprocess
import sys
import time
from contextlib import contextmanager
from pathlib import Path

try:
    from your_singer_ml import compute_device
except ImportError:  # 専用Pythonでは同階層のファイルを直接読む。
    import importlib.util as _util

    _spec = _util.spec_from_file_location(
        "compute_device", Path(__file__).with_name("compute_device.py")
    )
    compute_device = _util.module_from_spec(_spec)
    _spec.loader.exec_module(compute_device)


# {
#   責務: [require_training_runtime: CUDAと公式が要求する日本語辞書APIを確認する]
#   エラー: [CPU版PyTorch・辞書APIや日本語辞書の欠落を拒否する]
# }
def require_training_runtime() -> None:
    import torch

    if not torch.cuda.is_available():
        raise RuntimeError("Style-Bert-VITS2学習にはCUDA対応GPUと対応する専用PyTorchが必要です。")
    import pyopenjtalk

    if not callable(getattr(pyopenjtalk, "unset_user_dict", None)):
        raise RuntimeError("専用runtimeには公式が要求するpyopenjtalk-dictが必要です。")
    if not Path(os.fsdecode(pyopenjtalk.OPEN_JTALK_DICT_DIR)).is_dir():
        raise FileNotFoundError("専用runtimeにOpenJTalk日本語辞書が同梱されていません。")


# {
#   責務: [initialize: 公式既定値からジョブ専用の学習設定を作る]
#   処理: [1: CUDAを確認する 2: 公式設定を読み込む 3: 入出力先を固定する 4: 初期重みを複製する]
#   エラー: [GPUや固定版資源が不足する場合に失敗する]
# }
def initialize(trainer: Path, output: Path) -> None:
    import yaml

    require_training_runtime()
    request = json.loads((output / "request.json").read_text(encoding="utf-8"))
    model_name = request["model_name"]
    data = output / "data"
    # YAMLは専用Python内で読み込み、MLワーカーの依存と混ぜない。
    settings = yaml.safe_load((trainer / "default_config.yml").read_text(encoding="utf-8"))
    settings.update(model_name=model_name, dataset_path=str(data))
    # 分散学習の環境変数を既存のシェルから継承しない。1ジョブ1GPUで起動する。
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        settings["train_ms"]["env"].update(
            MASTER_ADDR="127.0.0.1",
            MASTER_PORT=reservation.getsockname()[1],
            WORLD_SIZE=1,
            LOCAL_RANK=0,
            RANK=0,
        )
    (output / "config.yml").write_text(
        yaml.safe_dump(settings, allow_unicode=True), encoding="utf-8"
    )
    configs = output / "configs"
    configs.mkdir()
    (configs / "paths.yml").write_text(
        yaml.safe_dump(dict(dataset_root=str(output), assets_root=str(output / "model_assets"))),
        encoding="utf-8",
    )
    hyperparameters = json.loads((trainer / "configs" / "config.json").read_text(encoding="utf-8"))
    if hyperparameters.get("version") != "2.7.0":
        raise RuntimeError("Style-Bert-VITS2学習設定は2.7.0が必要です。")
    hyperparameters["model_name"] = model_name
    hyperparameters["data"].update(
        training_files=str(data / "train.list"),
        validation_files=str(data / "val.list"),
        use_jp_extra=False,
    )
    # 既定はGPU+fp16(bf16非対応GPUでも可)。実効バッチは累積で維持する。
    plan = compute_device.plan_training(request)
    hyperparameters["train"].update(epochs=100, eval_interval=1000, log_interval=200)
    compute_device.apply_to_hyperparameters(hyperparameters["train"], plan)
    (output / "compute_plan.json").write_text(
        json.dumps(plan, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    # 公式モデル内のWavLM参照も、カレントディレクトリに依存させない。
    hyperparameters["model"].setdefault("slm", {})["model"] = str(
        trainer / "slm" / "wavlm-base-plus"
    )
    (data / "config.json").write_text(
        json.dumps(hyperparameters, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    if request.get("resolved_training_mode") == "from_scratch":
        # 最初から新規学習: 話者モデルの初期重みを置かない(暗黙の事前学習重みを使わない)。
        (data / "models").mkdir()
    else:
        shutil.copytree(trainer / "pretrained", data / "models")
    dictionary = output / "dict_data"
    if (trainer / "dict_data").is_dir():
        shutil.copytree(trainer / "dict_data", dictionary)
    else:
        dictionary.mkdir()


# {
#   責務: [isolated_dictionary_worker: 別ジョブと共有しない日本語辞書ワーカーを起動する]
#   処理: [1: 空きポートを選ぶ 2: 完全なモジュール名で起動する 3: 終了時に子プロセスを停止する]
#   エラー: [起動失敗と接続タイムアウトで失敗する]
# }
@contextmanager
def isolated_dictionary_worker():
    from style_bert_vits2.nlp.japanese import pyopenjtalk_worker
    from style_bert_vits2.nlp.japanese.pyopenjtalk_worker.worker_client import WorkerClient

    # 公式の起動関数はcwdからモジュール名を計算するため、ジョブ外のソースでは利用できない。
    with socket.socket() as reservation:
        reservation.bind((socket.gethostname(), 0))
        port = reservation.getsockname()[1]
    command = [
        sys.executable,
        "-m",
        "style_bert_vits2.nlp.japanese.pyopenjtalk_worker",
        "--port",
        str(port),
    ]
    process = subprocess.Popen(
        command,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        stdout=sys.stderr,
        stderr=sys.stderr,
    )
    try:
        deadline = time.monotonic() + 30
        while True:
            if process.poll() is not None:
                raise RuntimeError("日本語辞書ワーカーの起動に失敗しました。")
            try:
                client = WorkerClient(port)
                client.close()
                break
            except (OSError, socket.timeout):
                if time.monotonic() >= deadline:
                    raise TimeoutError("日本語辞書ワーカーへ接続できません。")
                time.sleep(0.1)
        # 既に起動済みのサーバーを指定し、公式の辞書IPCと終了処理を利用する。
        pyopenjtalk_worker.initialize_worker(port)
        yield
    finally:
        pyopenjtalk_worker.terminate_worker()
        if process.poll() is None:
            process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)


# { 責務: [_pyannote_embedding: 公式のstyle_gen.pyと同じ話者埋め込み(256次元)を計算する関数を作る] }
def _pyannote_embedding():
    import torch
    from pyannote.audio import Inference, Model

    model = Model.from_pretrained("pyannote/wespeaker-voxceleb-resnet34-LM")
    inference = Inference(model, window="whole")
    inference.to(torch.device("cuda" if torch.cuda.is_available() else "cpu"))
    return lambda path: inference(path)


# {
#   責務: [append_timbre_style: 歌唱由来の声質参照を、学習済みモデルの新しいスタイルとして追加する(経路T1)]
#   処理: [1: 参照音声の埋め込みを計算する 2: 使えないものを理由付きで除く 3: 平均をスタイルとして追記し、報告を残す]
#   エラー: [参照が無い・使える埋め込みが1件も無い・追記できない場合に失敗する(黙って省略しない)]
# }
def append_timbre_style(
    output: Path, references: Path, style_name: str, model_dir: Path, embed_factory=None
) -> dict:
    import importlib.util

    spec = importlib.util.spec_from_file_location(
        "style_bert_vits2_timbre", Path(__file__).with_name("style_bert_vits2_timbre.py")
    )
    timbre = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(timbre)
    wavs = sorted(references.glob("*.wav"))
    if not wavs:
        raise ValueError("声質参照の音声がありません。")
    vectors, skipped = timbre.compute_reference_vectors(
        [str(path) for path in wavs], (embed_factory or _pyannote_embedding)()
    )
    if vectors.shape[0] == 0:
        raise RuntimeError(
            "使える声質参照の埋め込みが1件もありません: "
            + "; ".join(f"{Path(p).name}: {r}" for p, r in skipped)
        )
    report = timbre.append_reference_style(model_dir, style_name, vectors)
    report["skipped"] = [dict(file=Path(path).name, reason=reason) for path, reason in skipped]
    (output / "timbre_style_report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return report


# {
#   責務: [install_weighted_sampler: 重みmanifestがあるジョブで、公式の DistributedBucketSampler を重み付き版へ差し替える]
#   処理: [1: request.json から重みmanifestの記録を読む 2: ジョブ内のmanifestをsha256で再検証する 3: data_utils のサンプラーを差し替える]
#   エラー: [manifestの改竄・欠落・固定版でないdata_utilsで失敗(等重みで続行しない)。記録が無い旧形式は何もしない]
# }
def install_weighted_sampler(trainer: Path, output: Path) -> None:
    request = json.loads((output / "request.json").read_text(encoding="utf-8"))
    record = request.get("weight_manifest")
    if record is None:
        return
    import importlib.util

    spec = importlib.util.spec_from_file_location(
        "style_bert_vits2_weighted_sampler",
        Path(__file__).with_name("style_bert_vits2_weighted_sampler.py"),
    )
    sampler = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(sampler)
    weights, digest = sampler.load_weight_manifest(
        output / "data" / record["file"], record["sha256"], request["source_job"]["job_id"]
    )
    import data_utils

    sampler.install(
        data_utils, weights, int(record.get("seed") or 0), digest, output / "weight_sampling.json"
    )


# {
#   責務: [run_training_with_fallback: fp16で学習し、失敗したらfp32で1回だけやり直す]
#   処理: [1: 計画がfp16/bf16ならautocastをfp16化する 2: 公式train_ms.pyを実行する 3: 失敗かつ混合精度ならconfigをfp32にして再実行する]
#   引数: [trainer: 公式ソースの場所。output: ジョブ出力先。arguments: train_ms.pyへ渡す引数]
#   戻り値: [なし]
#   エラー: [fp32でも失敗した場合、または元からfp32の失敗は呼出し元へ伝える]
# }
def run_training_with_fallback(trainer: Path, output: Path, arguments: list) -> None:
    config_path = output / "data" / "config.json"
    hyperparameters = json.loads(config_path.read_text(encoding="utf-8"))
    precision = hyperparameters["train"].get("mixed_precision", "fp32")
    script = str(trainer / "train_ms.py")
    for attempt in (precision, "fp32"):
        sys.argv = [script, *arguments]
        try:
            if attempt == "fp16":
                import torch

                original = compute_device.install_fp16_autocast(torch)
            runpy.run_path(script, run_name="__main__")
            return
        except Exception as error:
            if attempt == "fp32":
                raise
            print(f"混合精度({attempt})の学習に失敗したためfp32で再実行します: {error}", file=sys.stderr)
            compute_device.fallback_to_fp32(hyperparameters["train"])
            config_path.write_text(
                json.dumps(hyperparameters, ensure_ascii=False, indent=2), encoding="utf-8"
            )
        finally:
            if attempt == "fp16":
                torch.autocast = original


# {
#   責務: [main: 専用Pythonから許可した公式工程だけを起動する]
#   処理: [1: 公式ソースの版を確認する 2: 辞書の書込先を分離する 3: 工程を起動する]
#   エラー: [不明な工程・版違い・工程の失敗を終了コードで伝える]
# }
def main() -> None:
    trainer, output = (Path(argument).resolve() for argument in sys.argv[1:3])
    stage = sys.argv[3]
    allowed = {
        "initialize",
        "resample.py",
        "preprocess_text.py",
        "bert_gen.py",
        "style_gen.py",
        "train_ms.py",
        "timbre_style",
    }
    if stage not in allowed or Path.cwd().resolve() != output:
        raise ValueError("ジョブ専用環境の許可された工程のみ起動できます。")
    sys.path.insert(0, str(trainer))
    from style_bert_vits2 import constants

    if constants.VERSION != "2.7.0" or Path(constants.__file__).resolve().parent.parent != trainer:
        raise RuntimeError("同梱のStyle-Bert-VITS2 2.7.0ソースが必要です。")
    # user_dictモジュールのimportより前に指定し、同梱辞書の共有書込みを防ぐ。
    constants.DEFAULT_USER_DICT_DIR = output / "dict_data"
    if stage == "initialize":
        initialize(trainer, output)
        return
    if stage == "timbre_style":
        references, style_name, model_dir = sys.argv[4], sys.argv[5], sys.argv[6]
        append_timbre_style(
            output, Path(references).resolve(), style_name, Path(model_dir).resolve()
        )
        return
    if stage == "train_ms.py":
        install_weighted_sampler(trainer, output)
        run_training_with_fallback(trainer, output, sys.argv[4:])
        return
    sys.argv = [str(trainer / stage), *sys.argv[4:]]
    if stage in {"preprocess_text.py", "bert_gen.py"}:
        with isolated_dictionary_worker():
            runpy.run_path(str(trainer / stage), run_name="__main__")
    else:
        runpy.run_path(str(trainer / stage), run_name="__main__")


if __name__ == "__main__":
    main()
