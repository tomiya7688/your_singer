from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from .mfa_alignment_result import MfaAlignmentResultReader, MfaPronunciation
from .mfa_measurement import MfaMeasurementStore, audio_fingerprint


MFA_VERSION = "3.4.2"
MODEL_SHA256 = "85928ffb1024486872a677a92e1fa94d2f997462d0b84c73a58aa9bb0e35179a"
ALIGNMENT_METHOD = "mfa-japanese-phone-forced-alignment-v1"


# {
#   責務: [MfaForcedAligner: 同梱MFAで指定音素列の音声上の境界を実測する]
#   フィールド: [bundle_root: runtimeと固定モデルを持つ配布資源]
#   処理: [1: 資源を検証する 2: 区間専用の発音辞書でalign_oneを実行する 3: 結果を検証する]
# }
class MfaForcedAligner:
    # {
    #   責務: [__init__: 配布資源の保存先を確定する]
    #   引数: [bundle_root: テスト・構築時に指定する資源ルート。通常はworker横のmfa]
    # }
    def __init__(self, bundle_root: Path | None = None):
        configured_root = os.environ.get("YOURSINGER_MFA_BUNDLE")
        self.bundle_root = Path(
            bundle_root or configured_root or Path(sys.executable).parent / "mfa"
        ).resolve()

    # {
    #   責務: [validate_resources: 固定版runtimeと音響モデルを確認する]
    #   処理: [1: 必須ファイルとCondaの版を検証する 2: モデルSHA-256を検証する]
    #   戻り値: [resources: 専用Pythonと音響モデルのパス]
    #   エラー: [資源の欠落・版違い・改変で失敗する]
    # }
    def validate_resources(self) -> tuple[Path, Path]:
        runtime = self.bundle_root / "runtime"
        python_path = runtime / "python.exe"
        model_path = self.bundle_root / "models" / "japanese_mfa.zip"
        entry_path = Path(__file__).with_name("mfa_entry.py")
        soundfile_metadata = (
            self.bundle_root / "overlay" / "soundfile-0.13.1.dist-info" / "METADATA"
        )
        manifest_path = self.bundle_root / "bundle-manifest.json"
        notice_path = self.bundle_root / "MODEL-NOTICE.txt"
        if not all(
            path.is_file()
            for path in (
                python_path,
                model_path,
                entry_path,
                soundfile_metadata,
                manifest_path,
                notice_path,
            )
        ):
            raise FileNotFoundError(
                "音素時刻の実測に必要なMFA runtime・日本語モデル・起動処理が同梱されていません。"
            )
        package_records = list((runtime / "conda-meta").glob("montreal-forced-aligner-*.json"))
        if len(package_records) != 1:
            raise RuntimeError(f"MFA runtimeは{MFA_VERSION}が必要です。")
        try:
            package = json.loads(package_records[0].read_text(encoding="utf-8"))
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (ValueError, OSError) as error:
            raise RuntimeError("MFAの資源manifestまたはpackage記録を読み取れません。") from error
        if not isinstance(package, dict) or package.get("version") != MFA_VERSION:
            raise RuntimeError(f"MFA runtimeは{MFA_VERSION}が必要です。")
        if (
            not isinstance(manifest, dict)
            or manifest.get("schema_version") != 1
            or manifest.get("mfa_version") != MFA_VERSION
            or manifest.get("model_sha256") != MODEL_SHA256
        ):
            raise RuntimeError("MFAの資源manifestが固定版と一致しません。")
        if "Version: 0.13.1" not in soundfile_metadata.read_text(encoding="utf-8"):
            raise RuntimeError("MFA補助SoundFileは0.13.1が必要です。")
        with model_path.open("rb") as model_file:
            if hashlib.file_digest(model_file, "sha256").hexdigest() != MODEL_SHA256:
                raise RuntimeError("日本語MFA音響モデルのSHA-256が固定値と一致しません。")
        return python_path, model_path

    # {
    #   責務: [align: 一音素の対応を保った辞書で実音声をalign_oneへ渡す]
    #   処理: [1: 一時入力を作る 2: ローカルMFAを実行する 3: 実測時刻を読む]
    #   引数: [audio_path: 音声, phonemes: 内部音素列, duration_sec: 音声長, workspace: 私的な作業領域]
    #   戻り値: [timings: 検証済みの実測音素時刻]
    #   エラー: [未対応音素、欠損資源、タイムアウト、MFA失敗、不正出力で失敗する]
    # }
    def align(
        self, audio_path: Path, phonemes: list[str], duration_sec: float, workspace: Path
    ) -> list[dict]:
        model_phones = MfaPronunciation.encode(phonemes)
        python_path, model_path = self.validate_resources()
        scratch_root = workspace / "alignment"
        if not scratch_root.resolve().is_relative_to(workspace.resolve()):
            raise ValueError("MFAの作業領域がVoice Projectの外です。")
        scratch_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".mfa-", dir=scratch_root) as temporary_directory:
            run_root = Path(temporary_directory)
            (run_root / "utterance.lab").write_text("utterance", encoding="utf-8")
            (run_root / "utterance.dict").write_text(
                "utterance\t" + " ".join(model_phones) + "\n", encoding="utf-8"
            )
            audio_sha256 = audio_fingerprint(audio_path)
            try:
                self._run(python_path, model_path, audio_path.resolve(), run_root)
            except (RuntimeError, subprocess.TimeoutExpired) as error:
                # 次の調査に必要な子ログだけを、元音声や歌詞を含まない専用名で保存する。
                failure_log = scratch_root / (run_root.name + "-failure.log")
                if (run_root / "mfa.log").is_file():
                    shutil.copyfile(run_root / "mfa.log", failure_log)
                raise RuntimeError(
                    f"MFAの音素時刻実測に失敗しました。ログ: {failure_log.name}"
                ) from error
            output_path = run_root / "aligned.json"
            if not output_path.is_file():
                raise RuntimeError("MFAが音素時刻の出力を生成しませんでした。")
            return self._read_measurement(
                output_path, phonemes, duration_sec, audio_path, audio_sha256, scratch_root
            )

    # {
    #   責務: [_read_measurement: 結果と音声同一性を検証して測定記録を保存する]
    #   引数: [output_path: MFA結果, phonemes: 期待音素, duration_sec: 音声長,
    #           audio_path: 元音声, audio_sha256: 実行前hash, scratch_root: alignment領域]
    #   戻り値: [timings: 既存形式の実測音素列]
    #   エラー: [結果不正、入力変更、保存失敗を伝える]
    # }
    def _read_measurement(
        self,
        output_path: Path,
        phonemes: list[str],
        duration_sec: float,
        audio_path: Path,
        audio_sha256: str,
        scratch_root: Path,
    ) -> list[dict]:
        document = json.loads(output_path.read_text(encoding="utf-8"))
        timings = MfaAlignmentResultReader.read(document, phonemes, duration_sec)
        # 実行中に音声が変わった測定を保存・再利用しない。
        if audio_fingerprint(audio_path) != audio_sha256:
            raise RuntimeError("MFA実行中に元音声が変更されました。再解析してください。")
        MfaMeasurementStore(scratch_root / "measurements").save(
            document,
            timings,
            duration_sec,
            audio_sha256,
            {
                "alignment_method": ALIGNMENT_METHOD,
                "mfa_version": MFA_VERSION,
                "model_sha256": MODEL_SHA256,
            },
        )
        return timings

    # {
    #   責務: [_run: 区間専用の環境でMFAを起動する]
    #   処理: [1: 一時領域・キャッシュをVoice Project内へ固定する 2: 逐次ログへ出力し実行を待つ]
    #   引数: [python_path: 専用Python, model_path: 固定モデル, audio_path: 音声, run_root: 今回の作業領域]
    #   エラー: [10分で完了しない場合または終了コードが0以外の場合に失敗する]
    # }
    def _run(self, python_path: Path, model_path: Path, audio_path: Path, run_root: Path) -> None:
        environment = os.environ.copy()
        # ホスト側のPython設定を持ち込まず、子のDLLとモジュールを専用runtimeだけから読み込む。
        for key in ("PYTHONHOME", "PYTHONPATH", "PYTHONPYCACHEPREFIX"):
            environment.pop(key, None)
        for key in (
            "TEMP",
            "TMP",
            "HOME",
            "USERPROFILE",
            "LOCALAPPDATA",
            "APPDATA",
            "MFA_ROOT_DIR",
            "XDG_CACHE_HOME",
        ):
            directory = run_root / key.lower()
            directory.mkdir()
            environment[key] = str(directory)
        environment.update(
            PYTHONUTF8="1",
            PYTHONIOENCODING="utf-8",
            PYTHONDONTWRITEBYTECODE="1",
            HF_HUB_OFFLINE="1",
        )
        command = [
            str(python_path),
            str(Path(__file__).with_name("mfa_entry.py")),
            str(audio_path),
            str(run_root / "utterance.lab"),
            str(run_root / "utterance.dict"),
            str(model_path),
            str(run_root / "aligned.json"),
            "--output_format",
            "json",
            "--no_tokenization",
            "--clean",
            "--no_use_mp",
            "--temporary_directory",
            str(run_root / "scratch"),
        ]
        # 生の歌詞や音声パスを応答へ含めず、プロジェクト内の一時ログへ閉じ込める。
        with (run_root / "mfa.log").open("w", encoding="utf-8") as log_file:
            completed = subprocess.run(
                command,
                cwd=run_root,
                env=environment,
                stdout=log_file,
                stderr=subprocess.STDOUT,
                check=False,
                timeout=600,
            )
        if completed.returncode != 0:
            raise RuntimeError(
                f"MFAの音素時刻実測に失敗しました（終了コード{completed.returncode}）。"
            )


# {
#   責務: [check_forced_alignment_runtime: 同梱forced alignment資源の版と整合性を確認する]
#   処理: [1: 固定版とモデルhashを検証する]
#   引数: [payload: ワーカー共通の空要求]
#   戻り値: [status: 検証済み資源の版と方式]
#   エラー: [欠落・版違い・改変で失敗する]
# }
def check_forced_alignment_runtime(payload: dict) -> dict:
    MfaForcedAligner().validate_resources()
    return {
        "ready": True,
        "mfa_version": MFA_VERSION,
        "method": ALIGNMENT_METHOD,
        "model_sha256": MODEL_SHA256,
    }
