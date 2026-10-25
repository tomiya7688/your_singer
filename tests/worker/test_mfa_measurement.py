import hashlib
import json
import stat
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from your_singer_ml.mfa_alignment import MfaForcedAligner
from your_singer_ml.mfa_measurement import MAX_ALIGNMENT_AUDIO_BYTES, audio_fingerprint


# {
#   責務: [MfaMeasurementTests: 検証済み無音と未知区間が測定記録へ分離保存されることを確認する]
#   処理: [1: 架空のMFA結果を返す 2: 成功・失敗時の永続記録を照合する]
# }
class MfaMeasurementTests(unittest.TestCase):
    # { 責務: [setUp: 外部モデルを使わない架空音声と測定結果を用意する] }
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.audio = self.root / "audio.wav"
        self.audio.write_bytes(b"synthetic audio; not used by a real aligner")
        self.document = {
            "tiers": {
                "phones": {
                    "entries": [
                        [0.1, 0.2, "sil"],
                        [0.2, 0.4, "a"],
                        [0.4, 0.5, ""],
                        [0.6, 0.9, "i"],
                    ]
                }
            }
        }
        self.aligner = MfaForcedAligner(self.root)

    # { 責務: [run_alignment: 既存のalign入口を通して架空結果の検証・保存を実行する] }
    def run_alignment(self, mutate_audio=False):
        # { 責務: [write_result: 外部プロセスの代わりに測定結果と任意の入力変更を再現する] }
        def write_result(python_path, model_path, audio_path, run_root):
            (run_root / "aligned.json").write_text(json.dumps(self.document), encoding="utf-8")
            if mutate_audio:
                self.audio.write_bytes(b"modified during alignment")

        with (
            patch.object(
                self.aligner,
                "validate_resources",
                return_value=(self.root / "python", self.root / "model"),
            ),
            patch.object(self.aligner, "_run", side_effect=write_result),
        ):
            return self.aligner.align(self.audio, ["a", "i"], 1.0, self.root)

    # { 責務: [test_keeps_original_labels_and_unknown_gaps_separate: sil・空ラベル・未観測の隙間を分ける] }
    def test_keeps_original_labels_and_unknown_gaps_separate(self):
        timings = self.run_alignment()
        record = json.loads(
            next((self.root / "alignment/measurements").glob("*.json")).read_text(encoding="utf-8")
        )
        self.assertEqual(["a", "i"], [timing["phoneme"] for timing in timings])
        self.assertEqual(timings, record["phonemes"])
        self.assertEqual(
            [{"start_sec": 0.1, "end_sec": 0.2, "label": "sil"}], record["silence_intervals"]
        )
        self.assertEqual(
            [{"start_sec": 0.4, "end_sec": 0.5, "label": ""}], record["unlabelled_intervals"]
        )
        self.assertEqual(
            [(0.0, 0.1), (0.5, 0.6), (0.9, 1.0)],
            [(gap["start_sec"], gap["end_sec"]) for gap in record["unobserved_intervals"]],
        )
        self.assertFalse(record["breath_classification_run"])
        self.assertFalse(record["confidence_calibrated"])
        self.assertEqual(
            hashlib.sha256(self.audio.read_bytes()).hexdigest(), record["audio_sha256"]
        )
        self.assertEqual("3.4.2", record["conditions"]["mfa_version"])
        self.assertFalse(list((self.root / "alignment").glob(".mfa-*")))

    # { 責務: [test_repeated_alignment_retains_previous_record: 再実行で既存測定を上書きしない] }
    def test_repeated_alignment_retains_previous_record(self):
        self.run_alignment()
        first = next((self.root / "alignment/measurements").glob("*.json"))
        previous_bytes = first.read_bytes()
        self.run_alignment()
        self.assertEqual(2, len(list(first.parent.glob("*.json"))))
        self.assertEqual(previous_bytes, first.read_bytes())

    # { 責務: [test_process_reads_private_snapshot: 元音声が一時的に差し替わってもMFAへ固定コピーを渡す] }
    def test_process_reads_private_snapshot(self):
        original = self.audio.read_bytes()

        # { 責務: [read_snapshot: 元パスの一時変更中も測定入力の内容が固定されていることを確かめる] }
        def read_snapshot(python_path, model_path, audio_path, run_root):
            self.assertNotEqual(self.audio.resolve(), audio_path.resolve())
            self.audio.write_bytes(b"temporarily replaced input")
            try:
                self.assertEqual(original, audio_path.read_bytes())
            finally:
                self.audio.write_bytes(original)
            (run_root / "aligned.json").write_text(json.dumps(self.document), encoding="utf-8")

        with (
            patch.object(
                self.aligner,
                "validate_resources",
                return_value=(self.root / "python", self.root / "model"),
            ),
            patch.object(self.aligner, "_run", side_effect=read_snapshot),
        ):
            self.aligner.align(self.audio, ["a", "i"], 1.0, self.root)

    # { 責務: [test_invalid_or_changed_audio_does_not_publish_measurement: 不正結果と入力変更を公開しない] }
    def test_invalid_or_changed_audio_does_not_publish_measurement(self):
        with self.assertRaisesRegex(RuntimeError, "元音声"):
            self.run_alignment(mutate_audio=True)
        self.document["tiers"]["phones"]["entries"][1][2] = "spn"
        with self.assertRaises(ValueError):
            self.run_alignment()
        self.assertFalse((self.root / "alignment/measurements").exists())

    # { 責務: [test_save_failure_is_not_success: 保存失敗を伝え、一時JSONを残さない] }
    def test_save_failure_is_not_success(self):
        with patch(
            "your_singer_ml.mfa_measurement.Path.rename",
            side_effect=OSError("synthetic write failure"),
        ):
            with self.assertRaises(OSError):
                self.run_alignment()
        self.assertFalse(list((self.root / "alignment/measurements").iterdir()))

    # { 責務: [test_measurement_link_outside_alignment_is_rejected: 記録領域から外部へ書き出さない] }
    def test_measurement_link_outside_alignment_is_rejected(self):
        alignment = self.root / "alignment"
        alignment.mkdir()
        outside = self.root / "outside"
        outside.mkdir()
        try:
            (alignment / "measurements").symlink_to(outside, target_is_directory=True)
        except OSError:
            self.skipTest("この環境ではsymlink作成権限がありません。")
        with self.assertRaisesRegex(ValueError, "alignment領域内"):
            self.run_alignment()
        self.assertFalse(list(outside.iterdir()))

    # { 責務: [test_rejects_special_or_oversized_audio_before_read: 特殊ファイルと巨大入力を読み始めない] }
    def test_rejects_special_or_oversized_audio_before_read(self):
        for mode, size in ((stat.S_IFIFO, 1), (stat.S_IFREG, MAX_ALIGNMENT_AUDIO_BYTES + 1)):
            with (
                self.subTest(mode=mode),
                patch.object(
                    Path, "stat", return_value=SimpleNamespace(st_mode=mode, st_size=size)
                ),
                patch.object(Path, "open") as open_audio,
            ):
                with self.assertRaisesRegex(ValueError, "通常ファイル"):
                    audio_fingerprint(self.audio)
                open_audio.assert_not_called()

    # { 責務: [test_rejects_size_change_while_hashing: hash計算中の入力伸縮を拒否する] }
    def test_rejects_size_change_while_hashing(self):
        for size in (1, 1000):
            with (
                self.subTest(size=size),
                patch.object(
                    Path, "stat", return_value=SimpleNamespace(st_mode=stat.S_IFREG, st_size=size)
                ),
            ):
                with self.assertRaisesRegex(RuntimeError, "サイズ"):
                    audio_fingerprint(self.audio)


if __name__ == "__main__":
    unittest.main()
