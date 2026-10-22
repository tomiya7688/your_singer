from __future__ import annotations

import json
import os
import re
import shutil
from pathlib import Path

from .style_bert_vits2_bundle import StyleBertVits2Bundle


# {
#   責務: [StyleBertVits2TrainingSession: 学習入力とジョブ内の作業先を管理する]
#   フィールド: [trainer: 同梱公式ソース dataset: 確定済み学習入力 output: 学習ジョブの作業先]
#   処理: [1: 入力と資源を確認する 2: 設定と環境を分離する 3: 工程間の成果物を確認する]
# }
class StyleBertVits2TrainingSession:
    # {
    #   責務: [__init__: 学習要求の保存先とモデル名を確認する]
    #   エラー: [保存先の重複・パスとして危険なモデル名・入力外の出力先を拒否する]
    # }
    def __init__(self, payload: dict):
        self.payload = payload
        self.trainer = Path(payload["trainer_root"]).resolve()
        self.dataset = Path(payload["dataset_dir"]).resolve()
        self.output = Path(payload["output_dir"]).resolve()
        self.model_name = payload["model_name"]
        if (not isinstance(self.model_name, str) or not 1 <= len(self.model_name) <= 80
                or self.model_name in {".", ".."} or self.model_name.endswith((".", " "))
                or any(ord(char) < 32 or char in '<>:"/\\|?*' for char in self.model_name)
                or re.match(r"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", self.model_name, re.I)):
            raise ValueError("モデル名には80文字以内の有効なフォルダ名を指定してください。")
        if self.output != self.dataset / "trainer" or self.trainer == self.dataset or self.trainer in self.dataset.parents:
            raise ValueError("学習出力は入力ジョブ内のtrainerへ配置してください。")
        if self.dataset in self.trainer.parents:
            raise ValueError("Trainer資源は学習ジョブの外へ配置してください。")
        self.python = self.trainer / "runtime" / "python.exe"
        self.entry = Path(__file__).with_name("style_bert_vits2_entry.py")
        self.data = self.output / "data"
        self.assets = self.output / "model_assets" / self.model_name
        self.audio_names: set[str] = set()

    # {
    #   責務: [prepare: 確定済み入力の複製を新しい作業先へ保存する]
    #   処理: [1: 専用Pythonと公式資源を確認する 2: ジョブ照合を行う 3: 音声と書き起こしを複製する]
    #   エラー: [入力・資源の不足や既存出力の上書きを拒否する]
    # }
    def prepare(self) -> None:
        scripts = ("resample.py", "preprocess_text.py", "bert_gen.py", "style_gen.py", "train_ms.py",
                   "default_config.yml", "configs/config.json", "style_bert_vits2/constants.py")
        required = [self.python, self.entry, *(self.trainer / script for script in scripts)]
        required += [self.trainer / "pretrained" / name for name in ("G_0.safetensors", "D_0.safetensors", "DUR_0.safetensors")]
        if not all(path.is_file() and path.stat().st_size for path in required):
            raise FileNotFoundError("Style-Bert-VITS2 2.7.0の公式ソース・専用Python・事前学習重みが同梱されていません。")
        StyleBertVits2Bundle(self.trainer).validate()
        manifest = json.loads((self.dataset / "dataset.json").read_text(encoding="utf-8-sig"))
        expected = {"job_id": self.payload["job_id"], "dataset_fingerprint": self.payload["dataset_fingerprint"],
                    "auto_correction_enabled": self.payload["auto_correction_enabled"]}
        if any(manifest.get(key) != value for key, value in expected.items()):
            raise ValueError("トーク学習入力と要求ジョブの識別情報が一致しません。")
        self._require_input_lines(manifest)
        # 既存の学習結果や失敗時ログは再試行で置換しない。
        self.output.mkdir(exist_ok=False)
        self.data.mkdir()
        raw = self.data / "raw"
        raw.mkdir()
        for item in manifest["items"]:
            audio = Path(item["audio_path"]).resolve()
            shutil.copyfile(audio, raw / audio.name)
        shutil.copyfile(self.dataset / "esd.list", self.data / "esd.list")
        (self.output / "request.json").write_text(json.dumps(dict(model_name=self.model_name,
            source_job=expected), ensure_ascii=False, indent=2), encoding="utf-8")

    # {
    #   責務: [_require_input_lines: rawからの相対音声名・話者・日本語書き起こしを照合する]
    #   エラー: [入力外の音声・空文字・項目の欠落や重複・旧形式raw接頭辞を拒否する]
    # }
    def _require_input_lines(self, manifest: dict) -> None:
        items = manifest.get("items", [])
        if not items:
            raise ValueError("トーク学習入力が空です。")
        expected = []
        for item in items:
            audio = Path(item["audio_path"]).resolve()
            if audio.parent != self.dataset / "raw" or audio.suffix.lower() != ".wav" or not audio.is_file():
                raise ValueError("トーク学習音声が入力のrawフォルダ内にありません。")
            fields = [audio.name, item["speaker_name"], "JP", item["transcript"]]
            if any(not isinstance(field, str) or not field.strip() or any(char in field for char in "|\r\n") for field in fields):
                raise ValueError("トーク学習入力の列が空または不正です。")
            expected.append("|".join(fields))
            self.audio_names.add(audio.name)
        actual = (self.dataset / "esd.list").read_text(encoding="utf-8-sig").splitlines()
        if actual != expected or len(self.audio_names) != len(items):
            raise ValueError("esd.listとdataset.jsonのトーク学習項目が一致しません。入力を再作成してください。")

    # {
    #   責務: [command: 固定の工程を専用Pythonから起動するコマンドを作る]
    #   戻り値: [command: シェルを経由しない引数列]
    # }
    def command(self, stage: str) -> list[str]:
        arguments = {
            "initialize": [],
            "resample.py": ["-i", str(self.data / "raw"), "-o", str(self.data / "wavs"), "--sr", "44100", "--num_processes", "1"],
            "preprocess_text.py": ["--config-path", str(self.data / "config.json"), "--transcription-path", str(self.data / "esd.list"),
                "--train-path", str(self.data / "train.list"), "--val-path", str(self.data / "val.list"), "--val-per-lang", "0", "--yomi_error", "raise", "--correct_path"],
            "bert_gen.py": ["--config", str(self.data / "config.json")],
            "style_gen.py": ["--config", str(self.data / "config.json"), "--num_processes", "1"],
            "train_ms.py": ["--config", str(self.data / "config.json"), "--model", str(self.data), "--assets_root", str(self.assets.parent)],
        }
        return [str(self.python), str(self.entry), str(self.trainer), str(self.output), stage, *arguments[stage]]

    # {
    #   責務: [environment: キャッシュ・一時領域・Python探索先を専用環境へ固定する]
    #   エラー: [環境領域を作成できない場合に失敗する]
    # }
    def environment(self) -> dict[str, str]:
        environment = os.environ.copy()
        for name in ("PYTHONHOME", "PYTHONPATH", "PYTHONPYCACHEPREFIX", "OPEN_JTALK_DICT_DIR"):
            environment.pop(name, None)
        directories = {"TEMP": "temp", "TMP": "temp", "HOME": "home", "USERPROFILE": "home",
            "APPDATA": "app-data", "LOCALAPPDATA": "local-app-data", "HF_HOME": "cache/huggingface",
            "TORCH_HOME": "cache/torch", "XDG_CACHE_HOME": "cache", "NUMBA_CACHE_DIR": "cache/numba",
            "MPLCONFIGDIR": "cache/matplotlib", "PYTHONPYCACHEPREFIX": "cache/bytecode"}
        for name, relative in directories.items():
            directory = self.output / relative
            directory.mkdir(parents=True, exist_ok=True)
            environment[name] = str(directory)
        # 学習中に不足資源をダウンロードせず、配布資源の欠落として失敗させる。
        environment.update(PYTHONUTF8="1", HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1")
        environment["PYTHONPATH"] = str(self.trainer)
        # 取得済みモデルだけを共有し、音声由来の特徴と一時ファイルはジョブ内へ保存する。
        environment["HF_HUB_CACHE"] = str(self.trainer / "model-cache" / "huggingface" / "hub")
        environment["HUGGINGFACE_HUB_CACHE"] = environment["HF_HUB_CACHE"]
        environment["PYANNOTE_CACHE"] = str(self.trainer / "model-cache" / "torch" / "pyannote")
        for name in ("MASTER_ADDR", "MASTER_PORT", "WORLD_SIZE", "LOCAL_RANK", "RANK"):
            environment.pop(name, None)
        environment["PATH"] = os.pathsep.join((str(self.python.parent), str(self.python.parent / "Scripts"), environment.get("PATH", "")))
        return environment

    # {
    #   責務: [require_training_features: 前処理後の音声・BERT・スタイル特徴を確認する]
    #   エラー: [空の学習リスト・壊れた列・作業先外の音声・特徴不足を拒否する]
    # }
    def require_training_features(self) -> None:
        lines = (self.data / "train.list").read_text(encoding="utf-8").splitlines()
        if not lines:
            raise ValueError("公式前処理後のトーク学習入力が空です。trainer.logを確認してください。")
        lines += (self.data / "val.list").read_text(encoding="utf-8").splitlines()
        found_names = []
        for line in lines:
            columns = line.split("|")
            if len(columns) != 7:
                raise ValueError("公式前処理後のトーク学習入力が7列ではありません。")
            audio = Path(columns[0]).resolve()
            if audio.parent != self.data / "wavs":
                raise ValueError("公式前処理後の音声がジョブのwavsフォルダ内にありません。")
            found_names.append(audio.name)
            features = (audio, audio.with_suffix(".bert.pt"), Path(str(audio) + ".npy"))
            if not all(path.is_file() and path.stat().st_size for path in features):
                raise FileNotFoundError("公式前処理の音声・BERT・スタイル特徴が不足しています。")
        if set(found_names) != self.audio_names or len(found_names) != len(self.audio_names):
            raise ValueError("公式前処理で学習項目が欠落または重複しています。trainer.logを確認してください。")

    # {
    #   責務: [require_artifacts: 学習終了後の公式推論成果物の参照を取得する]
    #   エラー: [ゼロ終了でも設定・スタイル・学習重みが無い場合に失敗する]
    # }
    def require_artifacts(self) -> dict:
        config = self.assets / "config.json"
        styles = self.assets / "style_vectors.npy"
        pattern = re.compile(re.escape(self.model_name) + r"_e([1-9][0-9]*)_s([1-9][0-9]*)\.safetensors")
        weights = [path for path in self.assets.glob("*.safetensors") if pattern.fullmatch(path.name)]
        if not weights or not all(path.is_file() and path.stat().st_size for path in (config, styles, *weights)):
            raise FileNotFoundError("Trainerが終了しましたが、公式推論成果物が揃っていません。trainer.logを確認してください。")
        exported = json.loads(config.read_text(encoding="utf-8"))
        if exported.get("version") != "2.7.0" or exported.get("model_name") != self.model_name:
            raise ValueError("Trainerの推論設定の版またはモデル名が学習要求と一致しません。")
        return dict(config_path=str(config), style_vectors_path=str(styles), model_weights_paths=[str(path) for path in sorted(weights)])
