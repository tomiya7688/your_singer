from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

REPOSITORY = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPOSITORY / "tools"))
import verify_python_trust


# {
#   責務: [PythonTrustTests: pip・certifiの最低版判定とCA bundleの記録を確認する]
# }
class PythonTrustTests(unittest.TestCase):
    # { 責務: [test_version_comparison_is_numeric: 文字列順ではなく数値順で版を比べる] }
    def test_version_comparison_is_numeric(self):
        self.assertLess(verify_python_trust.parse_version("25.0.1"), verify_python_trust.parse_version("26.2.2"))
        self.assertLess(verify_python_trust.parse_version("2026.7.22"), verify_python_trust.parse_version("2026.10.19"))
        self.assertGreaterEqual(verify_python_trust.parse_version("2026.10.23"),
                                verify_python_trust.parse_version("2026.10.19"))

    # { 責務: [test_emsign_roots_are_listed_from_bundle_comments: bundleのコメントからemSign/eMudhra系だけを列挙する] }
    def test_emsign_roots_are_listed_from_bundle_comments(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "cacert.pem"
            bundle.write_text('# Issuer: CN=emSign Root CA - G1 O=eMudhra Technologies Limited\n'
                              '# Label: "emSign Root CA - G1"\n'
                              '# Issuer: CN=ISRG Root X1 O=Internet Security Research Group\n'
                              '-----BEGIN CERTIFICATE-----\n', encoding="utf-8")
            labels = verify_python_trust.find_emsign_roots(bundle)
        self.assertEqual(2, len(labels))
        self.assertTrue(all("ISRG" not in label for label in labels))

    # { 責務: [test_old_or_missing_packages_are_reported: 最低版未満と未導入をissuesへ記録する] }
    def test_old_or_missing_packages_are_reported(self):
        installed = {"pip": "25.0.1"}

        def fake_version(package: str) -> str:
            if package in installed:
                return installed[package]
            raise verify_python_trust.importlib.metadata.PackageNotFoundError(package)

        with patch.object(verify_python_trust.importlib.metadata, "version", side_effect=fake_version):
            report = verify_python_trust.collect_report()
        self.assertEqual(2, len(report["issues"]))
        self.assertTrue(any("pip 25.0.1" in issue for issue in report["issues"]))
        self.assertTrue(any("certifi" in issue for issue in report["issues"]))

    # { 責務: [test_optional_tools_are_not_required: uv・sigstoreが無くても失敗にしない] }
    def test_optional_tools_are_not_required(self):
        installed = {"pip": "26.2.2", "certifi": "2026.10.19"}

        def fake_version(package: str) -> str:
            if package in installed:
                return installed[package]
            raise verify_python_trust.importlib.metadata.PackageNotFoundError(package)

        with patch.object(verify_python_trust.importlib.metadata, "version", side_effect=fake_version), \
                patch.object(verify_python_trust, "find_emsign_roots", return_value=[]), \
                patch("certifi.where", return_value=__file__):
            report = verify_python_trust.collect_report()
        self.assertEqual([], report["issues"])


if __name__ == "__main__":
    unittest.main()
