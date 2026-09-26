from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import tempfile
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def copy_snapshot(source: Path, target: Path) -> list[dict]:
    if target.exists():
        shutil.rmtree(target)
    target.mkdir(parents=True)
    files: list[dict] = []
    for path in sorted(source.rglob("*")):
        if not path.is_file() or ".cache" in path.parts:
            continue
        relative = path.relative_to(source)
        destination = target / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, destination)
        files.append({
            "path": relative.as_posix(),
            "size": destination.stat().st_size,
            "sha256": sha256(destination),
        })
    if not files:
        raise RuntimeError(f"モデル資源が空です: {target.name}")
    return files


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    from huggingface_hub import snapshot_download

    lock_path = args.lock.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    lock_bytes = lock_path.read_bytes()
    config = json.loads(lock_bytes)
    manifest = {
        "schema_version": 1,
        "lock_sha256": hashlib.sha256(lock_bytes).hexdigest(),
        "resources": [],
    }

    with tempfile.TemporaryDirectory(prefix="your-singer-models-") as temp_root:
        root = Path(temp_root)
        for item in config["resources"]:
            temp = root / item["name"]
            snapshot_download(
                repo_id=item["repo_id"],
                revision=item["revision"],
                allow_patterns=item["allow_patterns"],
                local_dir=temp,
            )
            files = copy_snapshot(temp, output / item["name"])
            manifest["resources"].append({
                "name": item["name"],
                "repo_id": item["repo_id"],
                "revision": item["revision"],
                "license": item["license"],
                "files": files,
            })

    shutil.copy2(lock_path, output / "resource-lock.json")
    (output / "bundle-manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"補完用モデル資源を準備しました: {output}")


if __name__ == "__main__":
    main()
