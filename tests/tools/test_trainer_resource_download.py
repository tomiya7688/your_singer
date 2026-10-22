from __future__ import annotations

import hashlib
import http.client
import io
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

REPOSITORY = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPOSITORY / "tools"))
from prepare_style_bert_vits2_bundle import StyleBertVits2BundleBuilder


# {
#   責務: [TrainerResourceDownloadTests: 固定hashを維持した再取得と不完全ファイルの除去を確認する]
#   フィールド: [builder: 検証対象, payload: 公開素材を使わない応答, digest: 固定hash]
# }
class TrainerResourceDownloadTests(unittest.TestCase):
    # { 責務: [setUp: Eドライブのartifacts内に独立した検証cacheを作る] }
    def setUp(self):
        root = REPOSITORY / "artifacts/trainer-download-tests"
        root.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=root)
        self.addCleanup(self.temporary.cleanup)
        directory = Path(self.temporary.name)
        self.builder = StyleBertVits2BundleBuilder(directory / "output", directory / "cache")
        self.payload = b"fixed-resource-fixture"
        self.digest = hashlib.sha256(self.payload).hexdigest()
        self.urlopen = patch("prepare_style_bert_vits2_bundle.urllib.request.urlopen").start()
        self.addCleanup(patch.stopall)
        patch("prepare_style_bert_vits2_bundle.time.sleep").start()

    # { 責務: [test_valid_cache_skips_network: 検証済みcacheを再取得しない] }
    def test_valid_cache_skips_network(self):
        self.builder.cache.mkdir()
        cached = self.builder.cache / self.digest
        cached.write_bytes(self.payload)
        self.assertEqual(cached, self.builder.download("https://example.invalid/model", self.digest))
        self.urlopen.assert_not_called()

    # { 責務: [test_mismatch_retries_before_publish: 不一致応答を捨てて一致した応答だけを保存する] }
    def test_mismatch_retries_before_publish(self):
        self.urlopen.side_effect = [io.BytesIO(b"truncated"), io.BytesIO(self.payload)]
        downloaded = self.builder.download("https://example.invalid/model", self.digest)
        self.assertEqual(self.payload, downloaded.read_bytes())
        self.assertEqual(2, self.urlopen.call_count)
        self.assertEqual([downloaded], list(self.builder.cache.iterdir()))

    # { 責務: [test_repeated_mismatch_fails_with_diagnostics: 3回不一致なら実サイズとhashを報告して停止する] }
    def test_repeated_mismatch_fails_with_diagnostics(self):
        self.urlopen.side_effect = [io.BytesIO(b"bad") for _ in range(3)]
        with self.assertRaisesRegex(ValueError, f"size=3; expected={self.digest}; actual="):
            self.builder.download("https://example.invalid/model", self.digest)
        self.assertEqual(3, self.urlopen.call_count)
        self.assertEqual([], list(self.builder.cache.iterdir()))

    # { 責務: [test_interrupted_read_retries: 応答の途中切断も新しい一時ファイルで取得し直す] }
    def test_interrupted_read_retries(self):
        interrupted = MagicMock()
        interrupted.__enter__.return_value.read.side_effect = [b"partial", http.client.IncompleteRead(b"end")]
        self.urlopen.side_effect = [interrupted, io.BytesIO(self.payload)]
        downloaded = self.builder.download("https://example.invalid/model", self.digest)
        self.assertEqual(self.payload, downloaded.read_bytes())
        self.assertEqual(2, self.urlopen.call_count)
        self.assertEqual([downloaded], list(self.builder.cache.iterdir()))

    # { 責務: [test_network_error_preserves_existing_cache: 再取得対象外のエラーでは既存cacheを変更しない] }
    def test_network_error_preserves_existing_cache(self):
        self.builder.cache.mkdir()
        cached = self.builder.cache / self.digest
        cached.write_bytes(b"old-corrupt-cache")
        self.urlopen.side_effect = PermissionError("fixture")
        with self.assertRaises(PermissionError):
            self.builder.download("https://example.invalid/model", self.digest)
        self.assertEqual(b"old-corrupt-cache", cached.read_bytes())
        self.assertEqual([cached], list(self.builder.cache.iterdir()))


if __name__ == "__main__":
    unittest.main()
