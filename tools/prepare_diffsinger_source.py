from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import stat
import tempfile
import urllib.request
import zipfile
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[1]
ARTIFACTS = REPOSITORY / "artifacts"
LOCK_PATH = REPOSITORY / "src/process/YourSinger.Process/processing/ml/worker/resources/diffsinger-source.lock.json"


# {
#   責務: [require_artifact_path: 取得・展開先をリポジトリのartifacts内へ制限する]
#   引数: [path: 出力予定パス]
#   戻り値: [resolved: リンクを解決した絶対パス]
# }
def require_artifact_path(path: Path) -> Path:
    resolved = path.resolve()
    root = ARTIFACTS.resolve()
    if resolved == root or not resolved.is_relative_to(root):
        raise ValueError("取得・展開先はリポジトリのartifacts配下へ指定してください。")
    return resolved


# {
#   責務: [verify_archive: 固定commitのソース全体をSHA-256で照合する]
#   引数: [archive: 取得済みZIP, lock: 固定した取得情報]
# }
def verify_archive(archive: Path, lock: dict) -> None:
    with archive.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != lock["archive_sha256"]:
        raise ValueError("DiffSingerソースZIPのSHA-256が一致しません。")


# {
#   責務: [download_archive: 公開された固定ソースを専用一時ファイルへ取得する]
#   引数: [archive: artifacts内の保存先, lock: 取得URLとハッシュ]
#   エラー: [ハッシュ不一致時はキャッシュへ配置しない]
# }
def download_archive(archive: Path, lock: dict) -> None:
    archive.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".source-download-", dir=archive.parent) as temporary:
        partial = Path(temporary) / "source.zip"
        with urllib.request.urlopen(lock["archive_url"], timeout=120) as response, partial.open("wb") as output:
            shutil.copyfileobj(response, output)
        verify_archive(partial, lock)
        partial.replace(archive)


# {
#   責務: [verify_source: 公式入力コードと設定が固定版の内容であることを確認する]
#   引数: [source: 展開先, lock: 必須ファイルのハッシュ]
# }
def verify_source(source: Path, lock: dict) -> None:
    for relative, expected in lock["files"].items():
        if hashlib.sha256((source / relative).read_bytes()).hexdigest() != expected:
            raise ValueError(f"固定ソースの内容が一致しません: {relative}")


# {
#   責務: [extract_source: ZIP内の領域外参照とリンクを拒否して新規領域へ展開する]
#   引数: [archive: 検証済みZIP, destination: 新規展開先, lock: 固定commit]
#   エラー: [既存出力は変更せず、不正ZIPや検証失敗は一時領域だけを破棄する]
# }
def extract_source(archive: Path, destination: Path, lock: dict) -> None:
    if destination.exists() or destination.is_symlink():
        raise FileExistsError("DiffSingerソースは配置済みです。検証には--verify-onlyを使ってください。")
    destination.parent.mkdir(parents=True, exist_ok=True)
    prefix = f"DiffSinger-{lock['commit']}"
    with tempfile.TemporaryDirectory(prefix=".source-extract-", dir=destination.parent) as temporary:
        staging = Path(temporary).resolve()
        with zipfile.ZipFile(archive) as zipped:
            for entry in zipped.infolist():
                target = (staging / entry.filename).resolve()
                if (not target.is_relative_to(staging / prefix)
                        or stat.S_ISLNK(entry.external_attr >> 16) or "\\" in entry.filename):
                    raise ValueError("ソースZIPに不正なパスまたはリンクがあります。")
            zipped.extractall(staging)
        verify_source(staging / prefix, lock)
        (staging / prefix).rename(destination)


# {
#   責務: [main: 音響入力検証に使う公式ソースをEドライブのartifactsへ取得・照合する]
# }
def main() -> None:
    lock = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", type=Path, default=ARTIFACTS / "diffsinger-source/v2.5.1.zip")
    parser.add_argument("--output", type=Path,
                        default=ARTIFACTS / "diffsinger-source" / f"DiffSinger-{lock['commit']}")
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    archive, output = require_artifact_path(args.archive), require_artifact_path(args.output)
    if archive.is_relative_to(output) or output.is_relative_to(archive):
        raise ValueError("ソースZIPと展開先を別の領域にしてください。")
    if not archive.exists():
        if args.verify_only:
            raise FileNotFoundError("検証対象のソースZIPがありません。")
        download_archive(archive, lock)
    verify_archive(archive, lock)
    if not args.verify_only:
        extract_source(archive, output, lock)
    verify_source(output, lock)
    print(json.dumps({"source_version": lock["version"], "commit": lock["commit"],
                      "source_verified": True, "trainer_runtime_included": False}))


if __name__ == "__main__":
    main()
