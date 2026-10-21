from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY / "src/process/YourSinger.Process/processing/ml/worker"))
from your_singer_ml.mfa_alignment import MFA_VERSION, MODEL_SHA256, MfaForcedAligner

MODEL_URL = "https://github.com/MontrealCorpusTools/mfa-models/releases/download/acoustic-japanese_mfa-v3.0.0/japanese_mfa.zip"


# {
#   責務: [MfaBundleBuilder: MFA専用runtime・固定モデル・帰属情報をオフライン配布用にまとめる]
#   フィールド: [output: 検証後に公開するbundleの保存先]
#   処理: [1: source runtimeを検証する 2: 一時領域へ資源を集める 3: 固定値検証後に公開する]
# }
class MfaBundleBuilder:
    # {
    #   責務: [__init__: 出力先をリポジトリのartifacts配下に限定する]
    #   引数: [output: 出力先]
    #   エラー: [artifacts自体やその外を出力先にした場合に失敗する]
    # }
    def __init__(self, output: Path):
        self.output = output.resolve()
        artifact_root = (REPOSITORY / "artifacts").resolve()
        if self.output == artifact_root or not self.output.is_relative_to(artifact_root):
            raise ValueError("MFA bundleの出力先はartifacts内の専用フォルダにしてください。")

    # {
    #   責務: [build: 固定版のMFA配布資源を構築する]
    #   処理: [1: 入力と出力を検証する 2: 専用runtimeとモデルを配置する 3: 検証後に出力先へ移動する]
    #   引数: [runtime: Conda環境, acoustic_model: 既取得モデルまたはNone, soundfile_overlay: 既取得補助wheelまたはNone]
    #   エラー: [版・hashの不一致、コピーや取得失敗、出力先の既存時に失敗する]
    # }
    def build(self, runtime: Path, acoustic_model: Path | None, soundfile_overlay: Path | None) -> None:
        runtime = runtime.resolve(strict=True)
        if self.output.exists() or self.output.is_relative_to(runtime) or runtime.is_relative_to(self.output):
            raise ValueError("出力先は未作成で、入力runtimeと重ならない専用フォルダにしてください。")
        records = [json.loads(path.read_text(encoding="utf-8")) for path in (runtime / "conda-meta").glob("*.json")]
        mfa_records = [record for record in records if record.get("name") == "montreal-forced-aligner"]
        if len(mfa_records) != 1 or mfa_records[0].get("version") != MFA_VERSION:
            raise ValueError(f"入力runtimeにMFA {MFA_VERSION}が必要です。")
        self.output.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".mfa-bundle-", dir=self.output.parent) as temporary_directory:
            staging = Path(temporary_directory) / "bundle"
            # Condaのactivate用スクリプトに依存せず、Python・DLL・package metadataを一式配置する。
            shutil.copytree(runtime, staging / "runtime", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
            self._prepare_soundfile(staging, soundfile_overlay)
            model_path = staging / "models" / "japanese_mfa.zip"
            model_path.parent.mkdir()
            if acoustic_model is not None:
                shutil.copyfile(acoustic_model, model_path)
            else:
                # 版付き公式URLのみから取得し、後段で固定SHA-256を照合する。
                with urllib.request.urlopen(MODEL_URL, timeout=120) as response, model_path.open("wb") as output_file:
                    shutil.copyfileobj(response, output_file)
            self._write_notices(staging, records)
            MfaForcedAligner(staging).validate_resources()
            staging.rename(self.output)

    # {
    #   責務: [_prepare_soundfile: WindowsでDLLを同梱する固定SoundFile wheelを配置する]
    #   処理: [1: 既取得wheel資源をコピー、または固定版をpipで取得する]
    #   引数: [staging: bundle構築先, overlay: 既取得資源のルートまたはNone]
    #   エラー: [資源の不足またはpip失敗で失敗する]
    # }
    @staticmethod
    def _prepare_soundfile(staging: Path, overlay: Path | None) -> None:
        destination = staging / "overlay"
        if overlay is None:
            subprocess.run([sys.executable, "-m", "pip", "install", "--no-deps", "--target", str(destination),
                            "soundfile==0.13.1"], check=True)
        else:
            destination.mkdir()
            for name in ("soundfile.py", "_soundfile.py", "_soundfile_data", "soundfile-0.13.1.dist-info"):
                source = overlay / name
                if source.is_dir():
                    shutil.copytree(source, destination / name)
                else:
                    shutil.copyfile(source, destination / name)

    # {
    #   責務: [_write_notices: 固定モデルの帰属とConda依存の取得元を記録する]
    #   処理: [1: モデルの版・ライセンスを記録する 2: packageの出典とライセンスを記録する]
    #   引数: [staging: bundle構築先, records: Conda package metadata]
    # }
    @staticmethod
    def _write_notices(staging: Path, records: list[dict]) -> None:
        manifest = {
            "schema_version": 1, "mfa_version": MFA_VERSION, "model_version": "3.0.0",
            "model_url": MODEL_URL, "model_sha256": MODEL_SHA256,
            "model_license": "CC-BY-4.0", "soundfile_version": "0.13.1",
            "packages": [{key: record.get(key) for key in ("name", "version", "build", "license", "url", "sha256", "md5")}
                         for record in sorted(records, key=lambda record: record["name"])],
        }
        (staging / "bundle-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        (staging / "MODEL-NOTICE.txt").write_text(
            "Japanese MFA acoustic model v3.0.0\nMichael McAuliffe and Morgan Sonderegger\n"
            "https://github.com/MontrealCorpusTools/mfa-models\nCC BY 4.0\n"
            "https://creativecommons.org/licenses/by/4.0/\n"
            "The model is unchanged. Your Singer uses a custom per-utterance pronunciation dictionary.\n",
            encoding="utf-8")


# {
#   責務: [main: 構築引数または既存bundleの検証要求を受け付ける]
#   処理: [1: 引数を読む 2: 構築または固定資源の検証を行う]
# }
def main() -> None:
    parser = argparse.ArgumentParser(description="MFAのオフライン配布資源を構築します。")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--acoustic-model", type=Path)
    parser.add_argument("--soundfile-overlay", type=Path)
    parser.add_argument("--verify-only", action="store_true")
    options = parser.parse_args()
    if options.verify_only:
        MfaForcedAligner(options.output).validate_resources()
    else:
        if options.runtime is None:
            parser.error("構築時は--runtimeを指定してください。")
        MfaBundleBuilder(options.output).build(options.runtime, options.acoustic_model, options.soundfile_overlay)
    print("MFA bundle: verified")


if __name__ == "__main__":
    main()
