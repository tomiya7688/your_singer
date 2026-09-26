import hashlib
import importlib.metadata
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from your_singer_ml.runtime_preflight import REQUIRED_FILES, check_phoneme_supplement_runtime


class RuntimePreflightTests(unittest.TestCase):
    def make_bundle(self, root: Path):
        resources = []
        entries = [
            ("japanese-bert", "ku-nlp/deberta-v2-large-japanese-char-wwm", "dfd9b4461979b281b3b9f162e50066cc88f08ed3"),
            ("asr", "Systran/faster-whisper-small", "536b0662742c02347bc0e980a01041f333bce120"),
            ("speaker", "speechbrain/spkrec-ecapa-voxceleb", "0f99f2d0ebe89ac095bcc5903c4dd8f72b367286"),
        ]
        for name, repo, revision in entries:
            directory = root / name
            directory.mkdir(parents=True)
            files = []
            for relative in sorted(REQUIRED_FILES[name]):
                file = directory / relative
                file.parent.mkdir(parents=True, exist_ok=True)
                file.write_bytes((name + ":" + relative).encode())
                files.append({
                    "path": relative,
                    "size": file.stat().st_size,
                    "sha256": hashlib.sha256(file.read_bytes()).hexdigest(),
                })
            resources.append({
                "name": name, "repo_id": repo, "revision": revision, "license": "test",
                "files": files,
            })
        lock = root / "resource-lock.json"
        lock.write_text('{"fixture":true}', encoding="utf-8")
        manifest = {
            "schema_version": 1,
            "lock_sha256": hashlib.sha256(lock.read_bytes()).hexdigest(),
            "resources": resources,
        }
        (root / "bundle-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

    @staticmethod
    def version(name):
        return {
            "style-bert-vits2": "2.7.0",
            "speechbrain": "1.1.1",
            "faster-whisper": "1.1.1",
            "pyopenjtalk": "0.4.1",
        }[name]

    def test_bytes_dictionary_path_is_supported(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.make_bundle(root)
            fake = type("P", (), {"OPEN_JTALK_DICT_DIR": temp.encode()})()
            with patch.object(importlib.metadata, "version", self.version), patch.dict("sys.modules", {"pyopenjtalk": fake}):
                result = check_phoneme_supplement_runtime({"resources_root": temp})
        self.assertTrue(result["ready"])

    def test_missing_bundle_is_not_ready(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(importlib.metadata, "version", self.version):
            result = check_phoneme_supplement_runtime({"resources_root": temp})
        self.assertFalse(result["ready"])
        self.assertTrue(result["issues"])

    def test_manifest_revision_mismatch_is_not_ready(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.make_bundle(root)
            manifest = json.loads((root / "bundle-manifest.json").read_text())
            manifest["resources"][0]["revision"] = "wrong"
            (root / "bundle-manifest.json").write_text(json.dumps(manifest))
            fake = type("P", (), {"OPEN_JTALK_DICT_DIR": temp})()
            with patch.object(importlib.metadata, "version", self.version), patch.dict("sys.modules", {"pyopenjtalk": fake}):
                result = check_phoneme_supplement_runtime({"resources_root": temp})
        self.assertFalse(result["ready"])
        self.assertTrue(any("revision" in issue for issue in result["issues"]))

    def test_missing_required_manifest_entry_is_not_ready(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.make_bundle(root)
            manifest_path = root / "bundle-manifest.json"
            manifest = json.loads(manifest_path.read_text())
            item = next(x for x in manifest["resources"] if x["name"] == "asr")
            item["files"] = [x for x in item["files"] if x["path"] != "model.bin"]
            (root / "asr" / "model.bin").unlink()
            manifest_path.write_text(json.dumps(manifest))
            fake = type("P", (), {"OPEN_JTALK_DICT_DIR": temp})()
            with patch.object(importlib.metadata, "version", self.version), patch.dict("sys.modules", {"pyopenjtalk": fake}):
                result = check_phoneme_supplement_runtime({"resources_root": temp})
        self.assertFalse(result["ready"])
        self.assertTrue(any("必須ファイル" in issue for issue in result["issues"]))

    def test_full_verify_detects_changed_file(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.make_bundle(root)
            (root / "asr" / "model.bin").write_bytes(b"changed")
            fake = type("P", (), {"OPEN_JTALK_DICT_DIR": temp})()
            with patch.object(importlib.metadata, "version", self.version), patch.dict("sys.modules", {"pyopenjtalk": fake}):
                result = check_phoneme_supplement_runtime({"resources_root": temp, "full_verify": True})
        self.assertFalse(result["ready"])
        self.assertTrue(any("サイズ" in issue or "SHA-256" in issue for issue in result["issues"]))


if __name__ == "__main__":
    unittest.main()
