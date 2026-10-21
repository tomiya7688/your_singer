import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from your_singer_ml import style_bert_vits2_bundle as bundle_module


# {
#   責務: [StyleBertVits2BundleTests: 固定版資源の欠落・改変・範囲外参照を確認する]
#   処理: [1: 学習に使えない小さな合成bundleを作る 2: 整合性境界を確認する]
# }
class StyleBertVits2BundleTests(unittest.TestCase):
    # { 責務: [setUp: 実モデルを取得しない検証用資源を作成する] }
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name) / "bundle"
        self.root.mkdir()
        required = ("runtime/python.exe", "runtime/python312.dll", "LICENSE", "LGPL_LICENSE", "MODEL-NOTICES.json",
                    "train_ms.py", "style_gen.py", "configs/config.json", "default_config.yml")
        for relative in required:
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic-test-resource")
        self.model = self.root / "models" / "weights.bin"
        self.model.parent.mkdir()
        self.model.write_bytes(b"synthetic-test-model")
        identity = dict(schema_version=1, version="2.7.0", source_revision=bundle_module.SOURCE_REVISION,
                        source_sha256=bundle_module.SOURCE_SHA256)
        self.lock = dict(**identity, resources=[dict(name="test", destination="models", files=[dict(
            path="weights.bin", size=self.model.stat().st_size, sha256=bundle_module.file_sha256(self.model))])])
        self.manifest = dict(**identity, resource_lock_sha256=bundle_module.resource_lock_sha256(self.lock),
            runtime=dict(python="3.12.10", packages={"torch": "2.3.1+cu121", "torchaudio": "2.3.1+cu121", "numpy": "1.26.4", "pyopenjtalk-dict": "0.3.4.dev2"}))
        # 実配布用の固定lockと混同しないよう、このfixtureだけ別hashを使う。
        override = patch.object(bundle_module, "RESOURCE_LOCK_SHA256", self.manifest["resource_lock_sha256"])
        override.start()
        self.addCleanup(override.stop)
        self.save_metadata()

    # { 責務: [save_metadata: 合成bundleの追跡情報を保存する] }
    def save_metadata(self):
        for name, record in (("resource-lock.json", self.lock), ("bundle-manifest.json", self.manifest)):
            (self.root / name).write_text(json.dumps(record, ensure_ascii=False), encoding="utf-8")

    # { 責務: [test_full_verification_checks_file_hashes: 通常照合と全hash照合を確認する] }
    def test_full_verification_checks_file_hashes(self):
        bundle = bundle_module.StyleBertVits2Bundle(self.root)
        self.assertEqual(bundle.validate()["version"], "2.7.0")
        bundle.validate(full_verify=True)
        self.model.write_bytes(b"x" * self.model.stat().st_size)
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            bundle.validate(full_verify=True)

    # { 責務: [test_missing_or_truncated_models_are_rejected: 欠落・途中取得のモデルを拒否する] }
    def test_missing_or_truncated_models_are_rejected(self):
        self.model.write_bytes(b"partial")
        with self.assertRaises(FileNotFoundError):
            bundle_module.StyleBertVits2Bundle(self.root).validate()
        self.model.unlink()
        with self.assertRaises(FileNotFoundError):
            bundle_module.StyleBertVits2Bundle(self.root).validate()

    # { 責務: [test_fixed_runtime_and_source_are_required: CPU版runtimeとソース版違いを拒否する] }
    def test_fixed_runtime_and_source_are_required(self):
        for field, value in (("source_revision", "different"), ("version", "different")):
            original = copy.deepcopy(self.manifest)
            self.manifest[field] = value
            self.save_metadata()
            with self.assertRaises(ValueError):
                bundle_module.StyleBertVits2Bundle(self.root).validate()
            self.manifest = original
        self.manifest["runtime"]["packages"]["torch"] = "2.3.1+cpu"
        self.save_metadata()
        with self.assertRaisesRegex(ValueError, "torch"):
            bundle_module.StyleBertVits2Bundle(self.root).validate()

    # { 責務: [test_replaced_resource_lock_is_rejected: manifestを書き換えても別lockを受け付けないことを確認する] }
    def test_replaced_resource_lock_is_rejected(self):
        self.lock["resources"] = []
        self.manifest["resource_lock_sha256"] = bundle_module.resource_lock_sha256(self.lock)
        self.save_metadata()
        with self.assertRaisesRegex(ValueError, "resource lock"):
            bundle_module.StyleBertVits2Bundle(self.root).validate()

    # { 責務: [test_line_endings_do_not_change_the_resource_identity: checkout時の改行変換が固定値へ影響しないことを確認する] }
    def test_line_endings_do_not_change_the_resource_identity(self):
        path = self.root / "resource-lock.json"
        path.write_bytes(json.dumps(self.lock, indent=2).replace("\n", "\r\n").encode("utf-8"))
        bundle_module.StyleBertVits2Bundle(self.root).validate(full_verify=True)

    # { 責務: [test_model_paths_cannot_escape_the_bundle: 個別資源のパス検証も保存先外の参照を拒否する] }
    def test_model_paths_cannot_escape_the_bundle(self):
        outside = self.root.parent / "outside.bin"
        outside.write_bytes(b"outside")
        resource = dict(name="outside", destination="..", files=[dict(path="outside.bin", size=7, sha256=hashlib.sha256(b"outside").hexdigest())])
        with self.assertRaises(FileNotFoundError):
            bundle_module.StyleBertVits2Bundle(self.root)._validate_model(resource, True)


if __name__ == "__main__":
    unittest.main()
