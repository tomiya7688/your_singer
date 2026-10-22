from __future__ import annotations

import hashlib
import json
from pathlib import Path

SOURCE_REVISION = "d8148f3090ee5038ca7b4e4b327116c64467f952"
SOURCE_SHA256 = "6f6ff4555c1857ae4bcbca6fd3eff28cca8775fc09caf4009dd8bbad1bb5a47e"
RESOURCE_LOCK_SHA256 = "27a3740b0e485cbe56e0e42f97fcebcf8bdacf7aa26fd08b5500a5df32202089"


# { 責務: [file_sha256: 大きなモデルを全件メモリへ載せずSHA-256を計算する] }
def file_sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


# { 責務: [resource_lock_sha256: Windowsの改行形式に依存せず固定資源の内容を照合する] }
def resource_lock_sha256(lock: dict) -> str:
    canonical = json.dumps(lock, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


# {
#   責務: [StyleBertVits2Bundle: 専用runtimeと固定版モデルのオフライン資源を確認する]
#   フィールド: [root: 配布資源の保存先]
# }
class StyleBertVits2Bundle:
    # { 責務: [__init__: 配布資源の絶対パスを保持する] }
    def __init__(self, root: Path):
        self.root = root.resolve()

    # {
    #   責務: [validate: 固定ソース・runtime・モデルの欠落と不整合を拒否する]
    #   処理: [1: 追跡情報を照合する 2: 全モデルの存在とサイズを確認する 3: 指定時にhashを確認する]
    #   引数: [full_verify: 全モデルのhashを再計算する場合はTrue]
    #   エラー: [資源の欠落・版違い・改変で失敗する]
    # }
    def validate(self, full_verify: bool = False) -> dict:
        manifest_path, lock_path = self.root / "bundle-manifest.json", self.root / "resource-lock.json"
        if not manifest_path.is_file() or not lock_path.is_file():
            raise FileNotFoundError("Style-Bert-VITS2の固定資源manifestとresource lockが同梱されていません。")
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        lock = json.loads(lock_path.read_text(encoding="utf-8"))
        for record in (manifest, lock):
            if (record.get("schema_version") != 1 or record.get("version") != "2.7.0"
                    or record.get("source_revision") != SOURCE_REVISION or record.get("source_sha256") != SOURCE_SHA256):
                raise ValueError("Style-Bert-VITS2の固定版・ソース取得情報が一致しません。")
        if resource_lock_sha256(lock) != RESOURCE_LOCK_SHA256 or manifest.get("resource_lock_sha256") != RESOURCE_LOCK_SHA256:
            raise ValueError("Style-Bert-VITS2のresource lockが変更されています。")
        packages = manifest.get("runtime", {}).get("packages", {})
        if manifest.get("runtime", {}).get("python") != "3.12.10":
            raise ValueError("Style-Bert-VITS2の専用Pythonの版が一致しません。")
        for package, version in {"torch": "2.3.1+cu121", "torchaudio": "2.3.1+cu121", "numpy": "1.26.4", "pyopenjtalk-dict": "0.3.4.dev2"}.items():
            if packages.get(package) != version:
                raise ValueError(f"Style-Bert-VITS2の専用runtimeの固定版が一致しません: {package}")
        required = ("runtime/python.exe", "runtime/python312.dll", "LICENSE", "LGPL_LICENSE", "MODEL-NOTICES.json",
                    "train_ms.py", "style_gen.py", "configs/config.json", "default_config.yml")
        if not all((self.root / path).is_file() for path in required):
            raise FileNotFoundError("Style-Bert-VITS2のソース・専用runtime・ライセンスが不足しています。")
        for resource in lock["resources"]:
            self._validate_model(resource, full_verify)
        return manifest

    # { 責務: [_validate_model: 単一の固定モデルと必要ファイルを範囲内で確認する] }
    def _validate_model(self, resource: dict, full_verify: bool) -> None:
        destination = (self.root / resource["destination"]).resolve()
        if not destination.is_relative_to(self.root):
            raise FileNotFoundError("固定モデルの保存先がbundle内にありません。")
        if resource["name"] == "style-speaker":
            reference = destination / "refs" / "main"
            if not reference.is_file() or reference.read_text(encoding="utf-8") != resource["revision"]:
                raise ValueError("スタイル話者モデルのオフライン参照が一致しません。")
            destination = destination / "snapshots" / resource["revision"]
        for entry in resource["files"]:
            path = (destination / entry["path"]).resolve()
            if not path.is_relative_to(self.root) or not path.is_file() or path.stat().st_size != entry["size"]:
                raise FileNotFoundError(f"固定モデルのファイルが不足または不正です: {resource['name']}/{entry['path']}")
            if full_verify and file_sha256(path) != entry["sha256"]:
                raise ValueError(f"固定モデルのSHA-256が一致しません: {resource['name']}/{entry['path']}")
