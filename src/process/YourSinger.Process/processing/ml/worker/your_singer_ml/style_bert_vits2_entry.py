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
        settings["train_ms"]["env"].update(MASTER_ADDR="127.0.0.1", MASTER_PORT=reservation.getsockname()[1],
                                         WORLD_SIZE=1, LOCAL_RANK=0, RANK=0)
    (output / "config.yml").write_text(yaml.safe_dump(settings, allow_unicode=True), encoding="utf-8")
    configs = output / "configs"
    configs.mkdir()
    (configs / "paths.yml").write_text(yaml.safe_dump(dict(dataset_root=str(output),
        assets_root=str(output / "model_assets"))), encoding="utf-8")
    hyperparameters = json.loads((trainer / "configs" / "config.json").read_text(encoding="utf-8"))
    if hyperparameters.get("version") != "2.7.0":
        raise RuntimeError("Style-Bert-VITS2学習設定は2.7.0が必要です。")
    hyperparameters["model_name"] = model_name
    hyperparameters["data"].update(training_files=str(data / "train.list"),
        validation_files=str(data / "val.list"), use_jp_extra=False)
    hyperparameters["train"].update(batch_size=2, epochs=100, eval_interval=1000, log_interval=200, bf16_run=False)
    # 公式モデル内のWavLM参照も、カレントディレクトリに依存させない。
    hyperparameters["model"].setdefault("slm", {})["model"] = str(trainer / "slm" / "wavlm-base-plus")
    (data / "config.json").write_text(json.dumps(hyperparameters, ensure_ascii=False, indent=2), encoding="utf-8")
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
    command = [sys.executable, "-m", "style_bert_vits2.nlp.japanese.pyopenjtalk_worker", "--port", str(port)]
    process = subprocess.Popen(command, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                               stdout=sys.stderr, stderr=sys.stderr)
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


# {
#   責務: [main: 専用Pythonから許可した公式工程だけを起動する]
#   処理: [1: 公式ソースの版を確認する 2: 辞書の書込先を分離する 3: 工程を起動する]
#   エラー: [不明な工程・版違い・工程の失敗を終了コードで伝える]
# }
def main() -> None:
    trainer, output = (Path(argument).resolve() for argument in sys.argv[1:3])
    stage = sys.argv[3]
    allowed = {"initialize", "resample.py", "preprocess_text.py", "bert_gen.py", "style_gen.py", "train_ms.py"}
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
    sys.argv = [str(trainer / stage), *sys.argv[4:]]
    if stage in {"preprocess_text.py", "bert_gen.py"}:
        with isolated_dictionary_worker():
            runpy.run_path(str(trainer / stage), run_name="__main__")
    else:
        runpy.run_path(str(trainer / stage), run_name="__main__")


if __name__ == "__main__":
    main()
