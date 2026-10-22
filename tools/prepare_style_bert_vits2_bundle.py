from __future__ import annotations

import argparse
import hashlib
import http.client
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import zipfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[1]
LOCK_PATH = REPOSITORY / "src/process/YourSinger.Process/processing/ml/worker/resources/style-bert-vits2-training.lock.json"
sys.path.insert(0, str(REPOSITORY / "src/process/YourSinger.Process/processing/ml/worker"))
from your_singer_ml.style_bert_vits2_bundle import StyleBertVits2Bundle, file_sha256, resource_lock_sha256


# {
#   責務: [StyleBertVits2BundleBuilder: 固定ソース・専用runtime・モデルをオフライン用にまとめる]
#   フィールド: [output: 完成資源の保存先 cache: 検証済み取得物 lock: 固定版とhash]
#   処理: [1: ソースとモデルを取得する 2: runtimeを複製する 3: 整合性確認後に公開する]
# }
class StyleBertVits2BundleBuilder:
    # { 責務: [__init__: 出力と取得cacheをartifacts内の独立した専用フォルダへ限定する] }
    def __init__(self, output: Path, cache: Path):
        self.output, self.cache = output.resolve(), cache.resolve()
        artifacts = (REPOSITORY / "artifacts").resolve()
        for directory in (self.output, self.cache):
            if directory == artifacts or not directory.is_relative_to(artifacts):
                raise ValueError("構築先とcacheはartifacts内の専用フォルダにしてください。")
        if self.output == self.cache or self.output in self.cache.parents or self.cache in self.output.parents:
            raise ValueError("構築先と取得cacheは別の専用フォルダにしてください。")
        self.lock = json.loads(LOCK_PATH.read_text(encoding="utf-8"))

    # {
    #   責務: [download: 固定hashを確認して取得cacheへ保存する]
    #   処理: [1: 検証済みcacheを再利用する 2: 不完全な取得を最大3回試す 3: hash一致時に公開する]
    #   エラー: [取得失敗・繰り返すhash不一致で失敗し、不完全ファイルを再利用しない]
    # }
    def download(self, url: str, digest: str) -> Path:
        self.cache.mkdir(parents=True, exist_ok=True)
        destination = self.cache / digest
        if destination.is_file() and file_sha256(destination) == digest:
            return destination
        # 固定hashは変更せず、切断や不一致に限り新しい一時ファイルで取り直す。
        for attempt in range(1, 4):
            try:
                return self._download_attempt(url, digest, destination)
            except (http.client.IncompleteRead, ValueError) as error:
                if attempt == 3:
                    raise
                print(f"固定資源の取得を再試行します ({attempt}/3): {error}", file=sys.stderr, flush=True)
                time.sleep(attempt)
        raise AssertionError("固定資源の取得回数が不正です。")

    # {
    #   責務: [_download_attempt: 1回の応答を一時保存し、固定hash一致時だけcacheへ公開する]
    #   エラー: [不一致には実サイズとhashを添え、一時ファイルを除去して呼出元へ伝える]
    # }
    def _download_attempt(self, url: str, digest: str, destination: Path) -> Path:
        # 同じcacheへの並行構築でも一時ファイル名を共有しない。
        with tempfile.NamedTemporaryFile(prefix=".download-", dir=self.cache, delete=False) as temporary:
            staging = Path(temporary.name)
            try:
                with urllib.request.urlopen(url, timeout=180) as response:
                    shutil.copyfileobj(response, temporary, length=1024 * 1024)
                temporary.flush()
                actual_digest = file_sha256(staging)
                if actual_digest != digest:
                    raise ValueError(f"固定資源のSHA-256が一致しません: {url}; "
                                     f"size={staging.stat().st_size}; expected={digest}; actual={actual_digest}")
            except BaseException:
                temporary.close()
                staging.unlink(missing_ok=True)
                raise
        staging.replace(destination)
        return destination

    # { 責務: [prepare_downloads: 固定した公式ソースと4種類のモデルを取得する] }
    def prepare_downloads(self, source_archive: Path | None) -> Path:
        archive_url = f"https://codeload.github.com/litagin02/Style-Bert-VITS2/zip/{self.lock['source_revision']}"
        if source_archive is None:
            archive = self.download(archive_url, self.lock["source_sha256"])
        else:
            archive = source_archive.resolve(strict=True)
            if file_sha256(archive) != self.lock["source_sha256"]:
                raise ValueError("公式ソースarchiveのSHA-256が一致しません。")
        downloads = [(f"https://huggingface.co/{resource['repo_id']}/resolve/{resource['revision']}/{entry['path']}", entry["sha256"])
                     for resource in self.lock["resources"] for entry in resource["files"]]
        with ThreadPoolExecutor(max_workers=3) as executor:
            # mapを消費して取得失敗を構築元へ必ず伝える。
            list(executor.map(lambda download: self.download(*download), downloads))
        return archive

    # {
    #   責務: [build: 専用runtimeと固定資源を一時領域で組み立てる]
    #   エラー: [既存出力・入力と出力の重複・整合性不一致を拒否する]
    # }
    def build(self, runtime: Path, source_archive: Path | None) -> None:
        runtime = runtime.resolve(strict=True)
        if self.output.exists() or runtime == self.output or self.output in runtime.parents or runtime in self.output.parents:
            raise ValueError("構築先は未作成で、入力runtimeと重ならないフォルダにしてください。")
        if not (runtime / "python.exe").is_file() or (runtime / "pyvenv.cfg").exists():
            raise ValueError("移動可能な完全なWindows Python runtimeを指定してください。venvは使用できません。")
        self.require_runtime_packages(runtime)
        archive = self.prepare_downloads(source_archive)
        self.output.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".style-bert-vits2-", dir=self.output.parent) as temporary:
            staging = Path(temporary) / "bundle"
            staging.mkdir()
            self._extract_source(archive, staging)
            shutil.copytree(runtime, staging / "runtime", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
            self._copy_models(staging)
            shutil.copyfile(REPOSITORY / "tools/probe_style_bert_vits2_bundle.py", staging / "runtime-probe.py")
            self._write_manifest(staging)
            StyleBertVits2Bundle(staging).validate(full_verify=True)
            self.probe_runtime(staging)
            staging.rename(self.output)

    # { 責務: [_extract_source: 固定archiveの最上位フォルダを外し、範囲内のファイルだけを配置する] }
    @staticmethod
    def _extract_source(archive: Path, staging: Path) -> None:
        with zipfile.ZipFile(archive) as contents:
            for member in contents.infolist():
                relative = Path(*Path(member.filename).parts[1:])
                destination = (staging / relative).resolve()
                if not destination.is_relative_to(staging) or relative.is_absolute():
                    raise ValueError("ソースarchiveに保存先外のファイルがあります。")
                if not member.is_dir() and relative.parts:
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    with contents.open(member) as source, destination.open("wb") as target:
                        shutil.copyfileobj(source, target)

    # { 責務: [_copy_models: 検証済みモデルとオフラインcacheの固定main参照を配置する] }
    def _copy_models(self, staging: Path) -> None:
        for resource in self.lock["resources"]:
            destination = staging / resource["destination"]
            if resource["name"] == "style-speaker":
                destination = destination / "snapshots" / resource["revision"]
            destination.mkdir(parents=True, exist_ok=True)
            for entry in resource["files"]:
                shutil.copyfile(self.cache / entry["sha256"], destination / entry["path"])
            if resource["name"] == "style-speaker":
                reference = staging / resource["destination"] / "refs" / "main"
                reference.parent.mkdir()
                reference.write_text(resource["revision"], encoding="utf-8")

    # {
    #   責務: [probe_runtime: 複製先のPythonからモデルとCUDAの実ロードを確認する]
    #   処理: [1: 探索先を分離する 2: 専用probeを実行する 3: 失敗時も構築ログを保管する]
    #   エラー: [モデル・依存の実ロードに失敗した場合に完成bundleとして公開しない]
    # }
    def probe_runtime(self, staging: Path) -> None:
        verification = self.output.parent / (self.output.name + "-verification")
        verification.mkdir(parents=True, exist_ok=True)
        environment = os.environ.copy()
        for name in ("PYTHONHOME", "PYTHONPATH"):
            environment.pop(name, None)
        environment.update(PYTHONNOUSERSITE="1", PYTHONUTF8="1", PYTHONPYCACHEPREFIX=str(verification / "bytecode"))
        report = verification / "runtime-probe.json"
        log_path = verification / "runtime-probe.log"
        with log_path.open("w", encoding="utf-8") as log:
            completed = subprocess.run([str(staging / "runtime/python.exe"), str(staging / "runtime-probe.py"),
                "--root", str(staging), "--output", str(report)], check=False, stdout=log,
                stderr=subprocess.STDOUT, env=environment, cwd=verification)
        if completed.returncode != 0:
            raise RuntimeError(f"専用runtime・モデルの実ロードに失敗しました。 log={log_path}")
        shutil.copyfile(report, staging / "runtime-probe.json")

    # {
    #   責務: [require_runtime_packages: コピー前と移動後の専用Pythonの固定依存を照合する]
    #   処理: [1: Python探索先を分離する 2: 配布名の表記を正規化する 3: 固定版を照合する]
    #   エラー: [専用Pythonと依存の版が一致しない場合に失敗する]
    # }
    def require_runtime_packages(self, runtime: Path) -> dict:
        environment = os.environ.copy()
        for name in ("PYTHONHOME", "PYTHONPATH"):
            environment.pop(name, None)
        command = ("import importlib.metadata as m,json,platform,re; "
            "print(json.dumps(dict(python=platform.python_version(),packages="
            "{re.sub(r'[-_.]+','-',d.metadata['Name']).lower():d.version for d in m.distributions()})))")
        completed = subprocess.run([str(runtime / "python.exe"), "-c", command],
            check=True, capture_output=True, text=True, env=environment)
        inventory = json.loads(completed.stdout)
        if inventory["python"] != self.lock["python_version"]:
            raise ValueError("専用Pythonの版が固定値と一致しません。")
        expected = {"torch": "2.3.1+cu121", "torchaudio": "2.3.1+cu121", "numpy": "1.26.4", "pyopenjtalk-dict": "0.3.4.dev2"}
        # 主要4依存に加え、実検証した全依存の固定版をコピー前に照合する。
        requirements = REPOSITORY / "tools/style-bert-vits2-training-requirements.lock.txt"
        for line in requirements.read_text(encoding="utf-8-sig").splitlines():
            if line.strip() and not line.startswith("#"):
                package, separator, version = line.strip().partition("==")
                if not separator or not version:
                    raise ValueError("専用runtimeの依存lockが固定版形式ではありません。")
                expected[re.sub(r"[-_.]+", "-", package).lower()] = version
        for package, version in expected.items():
            if inventory["packages"].get(package) != version:
                raise ValueError(f"専用runtimeの固定依存が一致しません: {package}")
        return inventory

    # { 責務: [_write_manifest: 資源の固定値・runtimeの依存一覧・出典とライセンス確認状況を記録する] }
    def _write_manifest(self, staging: Path) -> None:
        runtime = self.require_runtime_packages(staging / "runtime")
        shutil.copyfile(LOCK_PATH, staging / "resource-lock.json")
        metadata = staging / "build-metadata"
        metadata.mkdir(exist_ok=True)
        for filename in ("style-bert-vits2-training-requirements.txt", "style-bert-vits2-training-requirements.lock.txt"):
            shutil.copyfile(REPOSITORY / "tools" / filename, metadata / filename)
        manifest = dict(schema_version=1, version=self.lock["version"], source_revision=self.lock["source_revision"],
            source_sha256=self.lock["source_sha256"], resource_lock_sha256=resource_lock_sha256(self.lock), runtime=runtime,
            redistribution_review_required=["pretrained model license is not declared in the upstream model card", "complete transitive license and notice review"])
        (staging / "bundle-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
        (staging / "MODEL-NOTICES.json").write_text(json.dumps(self.lock["resources"], ensure_ascii=False, indent=2), encoding="utf-8")


# { 責務: [main: 固定資源の取得・構築・既存bundleの整合性確認を実行する] }
def main() -> None:
    parser = argparse.ArgumentParser(description="Style-Bert-VITS2の専用オフライン資源を構築します。")
    parser.add_argument("--output", type=Path, default=REPOSITORY / "artifacts/style-bert-vits2-bundle")
    parser.add_argument("--cache", type=Path, default=REPOSITORY / "artifacts/style-bert-vits2-downloads")
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--source-archive", type=Path)
    parser.add_argument("--download-only", action="store_true")
    parser.add_argument("--verify-only", action="store_true")
    options = parser.parse_args()
    builder = StyleBertVits2BundleBuilder(options.output, options.cache)
    if options.verify_only:
        StyleBertVits2Bundle(options.output).validate(full_verify=True)
    elif options.download_only:
        builder.prepare_downloads(options.source_archive)
    else:
        if options.runtime is None:
            parser.error("構築時は--runtimeを指定してください。")
        builder.build(options.runtime, options.source_archive)
    print("Style-Bert-VITS2 resources: verified")


if __name__ == "__main__":
    main()
