import json
import tempfile
import unittest
from pathlib import Path

from your_singer_ml.run_report import build_run_report, sha256_file


# {
#   責務: [RunReportTests: 実行記録が成果物の相対パスとsha256を残し、不正な成果物を拒否することを確認する]
# }
class RunReportTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.output = Path(self.temporary.name) / "trainer"
        (self.output / "model_assets").mkdir(parents=True)
        self.weights = self.output / "model_assets" / "m.safetensors"
        self.weights.write_bytes(b"weights")

    def test_report_records_relative_paths_hashes_and_requested_values(self):
        report = build_run_report(
            "job-1", "fine_tune", 7, 0, self.output, [("model_weights", self.weights)]
        )
        self.assertEqual(report["schema_version"], "1")
        self.assertEqual(report["resolved_mode"], "fine_tune")
        self.assertIsNone(report["initialization_observed"])
        self.assertEqual(
            report["artifacts"],
            [
                dict(
                    kind="model_weights",
                    path="model_assets/m.safetensors",
                    sha256=sha256_file(self.weights),
                )
            ],
        )
        saved = json.loads((self.output / "run_manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(saved, report)

    def test_missing_empty_and_outside_artifacts_are_rejected(self):
        with self.assertRaises(FileNotFoundError):
            build_run_report("j", None, None, 0, self.output, [("w", self.output / "none.bin")])
        empty = self.output / "empty.bin"
        empty.write_bytes(b"")
        with self.assertRaises(FileNotFoundError):
            build_run_report("j", None, None, 0, self.output, [("w", empty)])
        outside = Path(self.temporary.name) / "outside.bin"
        outside.write_bytes(b"x")
        with self.assertRaises(ValueError):
            build_run_report("j", None, None, 0, self.output, [("w", outside)])


if __name__ == "__main__":
    unittest.main()
