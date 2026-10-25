from __future__ import annotations

import hashlib
import importlib.metadata
import json
import os
from pathlib import Path

EXPECTED = {
    "japanese-bert": ("ku-nlp/deberta-v2-large-japanese-char-wwm", "dfd9b4461979b281b3b9f162e50066cc88f08ed3"),
    "asr": ("Systran/faster-whisper-small", "536b0662742c02347bc0e980a01041f333bce120"),
    "speaker": ("speechbrain/spkrec-ecapa-voxceleb", "0f99f2d0ebe89ac095bcc5903c4dd8f72b367286"),
}
REQUIRED_FILES = {
    "japanese-bert": {
        "config.json",
        "model.safetensors",
        "special_tokens_map.json",
        "tokenizer_config.json",
        "vocab.txt",
    },
    "asr": {
        "config.json",
        "model.bin",
        "tokenizer.json",
        "vocabulary.txt",
    },
    "speaker": {
        "hyperparams.yaml",
        "embedding_model.ckpt",
        "mean_var_norm_emb.ckpt",
        "classifier.ckpt",
        "label_encoder.txt",
    },
}

PACKAGES = {
    "style-bert-vits2": "2.7.0",
    "speechbrain": "1.1.1",
    "faster-whisper": "1.1.1",
    "pyopenjtalk-dict": "0.3.4.dev2",
    "numpy": "1.26.4",
    "accelerate": "0.34.2",
    "setuptools": "80.9.0",
}


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


# {
#   責務: [_check_package_versions: 固定版のパッケージが同梱されているか確認する]
#   処理: [1: PACKAGES の各パッケージの版を取得する 2: 固定版と異なる・未同梱なら問題へ追加する]
#   引数: [versions: 取得した版の書き込み先, issues: 問題の書き込み先]
#   エラー: [未同梱は例外にせず問題として記録する]
# }
def _check_package_versions(versions: dict[str, str], issues: list[str]) -> None:
    for package, expected in PACKAGES.items():
        try:
            actual = importlib.metadata.version(package)
            versions[package] = actual
            if actual != expected:
                issues.append(f"{package} は {expected} が必要です。現在: {actual}")
        except importlib.metadata.PackageNotFoundError:
            issues.append(f"{package} がワーカーに同梱されていません。")


# {
#   責務: [_check_model_resources: manifest・lockと固定資源の一致を確認する]
#   処理: [1: manifestとlockの存在と整合を確認する 2: EXPECTED の各資源のフォルダと一覧を照合する]
#   引数: [root: モデル資源の配置先, full_verify: SHA-256まで検証するか, resources: 検証結果の書き込み先, issues: 問題の書き込み先]
#   エラー: [入出力・JSON不正は問題として記録し、それまでの検証結果は残す]
# }
def _check_model_resources(root: Path, full_verify: bool, resources: list[dict],
                           issues: list[str]) -> None:
    manifest_path = root / "bundle-manifest.json"
    lock_path = root / "resource-lock.json"
    if not manifest_path.is_file() or not lock_path.is_file():
        issues.append("補完用モデル資源のmanifestまたはlockがありません。")
        return
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest.get("schema_version") != 1:
            issues.append("補完用モデル資源のmanifest版に対応していません。")
        if manifest.get("lock_sha256") != _sha256(lock_path):
            issues.append("補完用モデル資源のlockとmanifestが一致しません。")
        by_name = {item.get("name"): item for item in manifest.get("resources", [])}
        for name, (repo_id, revision) in EXPECTED.items():
            item = by_name.get(name)
            if not isinstance(item, dict):
                issues.append(f"{name} のモデル資源がありません。")
                continue
            if item.get("repo_id") != repo_id or item.get("revision") != revision:
                issues.append(f"{name} の取得元またはrevisionが固定値と一致しません。")
            directory = root / name
            if not directory.is_dir():
                issues.append(f"{name} のモデルフォルダがありません。")
                continue
            file_entries = item.get("files")
            if not isinstance(file_entries, list) or not file_entries:
                issues.append(f"{name} のファイル一覧がありません。")
                continue
            checked = _check_resource_files(name, directory, file_entries, full_verify, issues)
            resources.append({"name": name, "checked_files": checked})
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        issues.append(f"補完用モデル資源を検証できません: {exc}")


# {
#   責務: [_check_resource_files: 1資源のファイル一覧と実ファイルを照合する]
#   処理: [1: 必須ファイルがmanifestにあるか確認する 2: 各ファイルの実在・サイズ・任意でSHA-256を確認する]
#   引数: [name: 資源名, directory: 資源フォルダ, file_entries: manifestのファイル一覧, full_verify: SHA-256まで検証するか, issues: 問題の書き込み先]
#   戻り値: [checked: 問題なく確認できたファイル数]
#   エラー: [不一致は問題として記録し、そのファイルを確認済みに数えない]
# }
def _check_resource_files(name: str, directory: Path, file_entries: list, full_verify: bool,
                          issues: list[str]) -> int:
    listed = {
        entry.get("path")
        for entry in file_entries
        if isinstance(entry, dict) and isinstance(entry.get("path"), str)
    }
    missing_required = sorted(REQUIRED_FILES[name].difference(listed))
    if missing_required:
        issues.append(
            f"{name} の必須ファイルがmanifestにありません: "
            + ", ".join(missing_required)
        )
    checked = 0
    for entry in file_entries:
        relative = entry.get("path")
        if not isinstance(relative, str) or not relative:
            issues.append(f"{name} のmanifestに不正なパスがあります。")
            continue
        path = directory / relative
        if path.is_symlink() or not path.is_file():
            issues.append(f"{name}/{relative} が実ファイルとして存在しません。")
            continue
        if path.stat().st_size != entry.get("size"):
            issues.append(f"{name}/{relative} のサイズがmanifestと一致しません。")
            continue
        if full_verify and _sha256(path) != entry.get("sha256"):
            issues.append(f"{name}/{relative} のSHA-256がmanifestと一致しません。")
            continue
        checked += 1
    return checked


# {
#   責務: [_check_pronunciation_dictionary: OpenJTalkの日本語発音辞書が同梱されているか確認する]
#   引数: [issues: 問題の書き込み先]
#   エラー: [読み込み失敗は例外にせず問題として記録する]
# }
def _check_pronunciation_dictionary(issues: list[str]) -> None:
    try:
        import pyopenjtalk
        dictionary = Path(os.fsdecode(pyopenjtalk.OPEN_JTALK_DICT_DIR))
        if not dictionary.is_dir():
            issues.append("OpenJTalkの日本語発音辞書が同梱されていません。")
    except Exception as exc:
        issues.append(f"OpenJTalkの日本語発音辞書を確認できません: {exc}")


# {
#   責務: [check_phoneme_supplement_runtime: 音素補完に必要なパッケージ・モデル資源・発音辞書の同梱状態を検証する]
#   処理: [1: 固定版パッケージを確認する 2: モデル資源のmanifestと実ファイルを照合する 3: OpenJTalk辞書を確認する]
#   引数: [payload: resources_root(モデル資源の配置先)と full_verify(SHA-256まで検証するか)を含む要求]
#   戻り値: [report: 準備完了か、版、検証したファイル数、問題の一覧]
#   エラー: [検証中の入出力・JSON不正は問題として一覧へ記録し、例外としては伝えない]
# }
def check_phoneme_supplement_runtime(payload: dict) -> dict:
    root = Path(str(payload.get("resources_root", ""))).resolve()
    full_verify = bool(payload.get("full_verify", False))
    issues: list[str] = []
    versions: dict[str, str] = {}
    resources: list[dict] = []

    _check_package_versions(versions, issues)
    _check_model_resources(root, full_verify, resources, issues)
    _check_pronunciation_dictionary(issues)

    return {
        "ready": not issues,
        "full_verify": full_verify,
        "versions": versions,
        "resources": resources,
        "issues": issues,
    }
