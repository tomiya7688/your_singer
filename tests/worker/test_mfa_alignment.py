from __future__ import annotations

import copy
import array
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from your_singer_ml.mfa_measurement_result import MfaMeasurementResult
from your_singer_ml.mfa_alignment import ALIGNMENT_METHOD, MODEL_SHA256, MfaForcedAligner
from your_singer_ml.mfa_alignment_result import MfaAlignmentResultReader, MfaPronunciation
from your_singer_ml.universal_features import _align_training_phonemes, extract_universal_features


# {
#   責務: [MfaResultValidationTests: 実測時刻を捏造せず音素対応と境界を検証する]
#   処理: [1: 正常な表記変換と時刻保持を確認する 2: 不正時刻・音素を拒否する]
# }
class MfaResultValidationTests(unittest.TestCase):
    # {
    #   責務: [setUp: 公開可能な架空のMFA結果を用意する]
    #   副作用: [documentへ無音を含む三音素の架空区間を保存する]
    # }
    def setUp(self):
        self.document = {
            "tiers": {
                "phones": {
                    "entries": [
                        [0.0, 0.1, "sil"],
                        [0.1, 0.23, "ɡ"],
                        [0.23, 0.7, "ɯ"],
                        [0.7, 0.9, "ɾ"],
                        [0.9, 1.0, "sil"],
                    ]
                }
            }
        }

    # {
    #   責務: [test_preserves_measured_boundaries_without_inventing_confidence: 境界保持と未校正信頼度を確認する]
    # }
    def test_preserves_measured_boundaries_without_inventing_confidence(self):
        timings = MfaAlignmentResultReader.read(self.document, ["g", "u", "r"], 1.0)
        self.assertEqual(["g", "u", "r"], [timing["phoneme"] for timing in timings])
        self.assertEqual([0.1, 0.23, 0.7], [timing["start_sec"] for timing in timings])
        self.assertEqual([0.23, 0.7, 0.9], [timing["end_sec"] for timing in timings])
        self.assertTrue(all(timing["confidence"] == 0.0 for timing in timings))

    # {
    #   責務: [test_rejects_invalid_measurements: 不正時刻や音素不一致を成功扱いしない]
    # }
    def test_rejects_invalid_measurements(self):
        for field, replacement in (
            (0, -0.1),
            (0, float("nan")),
            (0, 0.05),
            (1, 0.1),
            (1, 1.1),
            (2, "spn"),
        ):
            with self.subTest(field=field, replacement=replacement):
                document = copy.deepcopy(self.document)
                document["tiers"]["phones"]["entries"][1][field] = replacement
                with self.assertRaises(ValueError):
                    MfaAlignmentResultReader.read(document, ["g", "u", "r"], 1.0)
        with self.assertRaises(ValueError):
            MfaAlignmentResultReader.read(self.document, ["g", "u"], 1.0)

    # {
    #   責務: [test_rejects_geminate_without_splitting_a_measured_phone: 促音へ架空の境界を作らない]
    # }
    def test_rejects_geminate_without_splitting_a_measured_phone(self):
        for phones in ([], ["cl", "k", "a"], ["pau"], ["unknown"]):
            with self.subTest(phones=phones), self.assertRaises(ValueError):
                MfaPronunciation.encode(phones)


# {
#   責務: [MfaExecutionTests: 専用runtimeの実行と失敗条件を確認する]
#   処理: [1: 欠損資源を拒否する 2: 終了失敗を伝える 3: 方式の誤記録を防ぐ]
# }
class MfaExecutionTests(unittest.TestCase):
    # {
    #   責務: [test_missing_resources_fail_before_process_start: 必須依存の欠落を検出する]
    # }
    def test_missing_resources_fail_before_process_start(self):
        with tempfile.TemporaryDirectory() as directory, patch("subprocess.run") as run:
            with self.assertRaises(FileNotFoundError):
                MfaForcedAligner(Path(directory)).align(
                    Path("audio.wav"), ["a"], 1.0, Path(directory)
                )
            run.assert_not_called()

    # {
    #   責務: [test_modified_model_fails_resource_validation: 改変されたモデルを推定値への切替で隠さない]
    # }
    def test_modified_model_fails_resource_validation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "runtime" / "conda-meta").mkdir(parents=True)
            (root / "runtime" / "python.exe").write_bytes(b"not executed")
            (root / "runtime" / "conda-meta" / "montreal-forced-aligner-fixture.json").write_text(
                json.dumps({"version": "3.4.2"})
            )
            (root / "models").mkdir()
            (root / "models" / "japanese_mfa.zip").write_bytes(b"modified model")
            metadata = root / "overlay" / "soundfile-0.13.1.dist-info" / "METADATA"
            metadata.parent.mkdir(parents=True)
            metadata.write_text("Version: 0.13.1\n")
            (root / "bundle-manifest.json").write_text(
                json.dumps(
                    {"schema_version": 1, "mfa_version": "3.4.2", "model_sha256": MODEL_SHA256}
                )
            )
            (root / "MODEL-NOTICE.txt").write_text("synthetic notice")
            with self.assertRaisesRegex(RuntimeError, "SHA-256"):
                MfaForcedAligner(root).validate_resources()

    # {
    #   責務: [test_failed_run_retains_only_diagnostic_log: 失敗した一時実行を片付けて診断ログだけを残す]
    # }
    def test_failed_run_retains_only_diagnostic_log(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            # {
            #   責務: [fail_run: 診断ログを書いた後で子プロセス失敗を再現する]
            # }
            def fail_run(python_path, model_path, audio_path, run_root):
                (run_root / "mfa.log").write_text("synthetic failure", encoding="utf-8")
                raise RuntimeError("failed")

            aligner = MfaForcedAligner(root)
            (root / "audio.wav").write_bytes(b"synthetic audio")
            with (
                patch.object(
                    aligner,
                    "validate_resources",
                    return_value=(root / "python.exe", root / "model.zip"),
                ),
                patch.object(aligner, "_run", side_effect=fail_run),
            ):
                with self.assertRaises(RuntimeError):
                    aligner.align(root / "audio.wav", ["a"], 1.0, root)
            self.assertEqual(1, len(list((root / "alignment").glob("*-failure.log"))))
            self.assertFalse(any(path.is_dir() for path in (root / "alignment").iterdir()))

    # {
    #   責務: [test_nonzero_exit_is_not_a_successful_alignment: 子プロセス失敗を成功へ置き換えない]
    # }
    def test_nonzero_exit_is_not_a_successful_alignment(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch("subprocess.run", return_value=subprocess.CompletedProcess([], 9)) as run:
                with self.assertRaisesRegex(RuntimeError, "終了コード9"):
                    MfaForcedAligner(root)._run(
                        root / "python.exe", root / "model.zip", root / "audio.wav", root
                    )
                self.assertEqual(600, run.call_args.kwargs["timeout"])
                self.assertEqual(str(root / "temp"), run.call_args.kwargs["env"]["TEMP"])

    # {
    #   責務: [test_only_validated_measurement_gets_forced_alignment_method: 実測成功時だけ必須方式を付ける]
    # }
    def test_only_validated_measurement_gets_forced_alignment_method(self):
        measured = [{"phoneme": "a", "start_sec": 0.2, "end_sec": 0.9, "confidence": 0.0}]
        with patch.object(
            MfaForcedAligner,
            "measure",
            return_value=MfaMeasurementResult(
                measured, Path("alignment/measurements/example.json"), "1" * 64, "2" * 64
            ),
        ):
            timings, confidence, method, warning, reference = _align_training_phonemes(
                Path("a.wav"), Path("."), ["a"], 1.0, 0.99, []
            )
        self.assertEqual(measured, timings)
        self.assertEqual(ALIGNMENT_METHOD, method)
        self.assertEqual(0.0, confidence)
        self.assertIn("未校正", warning)
        self.assertEqual("alignment/measurements/example.json", reference["path"])

    # {
    #   責務: [test_unsupported_measurement_keeps_estimated_method: 対応できない音素の推定値を実測と誤記録しない]
    # }
    def test_unsupported_measurement_keeps_estimated_method(self):
        with (
            patch.object(MfaForcedAligner, "measure", side_effect=ValueError("unsupported phone")),
            patch("your_singer_ml.universal_features._phonemize", return_value=["a"]),
        ):
            _, _, method, warning, reference = _align_training_phonemes(
                Path("a.wav"),
                Path("."),
                ["a"],
                1.0,
                0.9,
                [{"text": "example", "start_sec": 0.1, "end_sec": 0.8, "confidence": 0.9}],
            )
        self.assertEqual("asr-word-boundary-proportional", method)
        self.assertEqual("unsupported phone", warning)
        self.assertIsNone(reference)

    # { 責務: [test_empty_phonemes_have_no_measurement_reference: 音素なしの応答へ実測参照を付けない] }
    def test_empty_phonemes_have_no_measurement_reference(self):
        with patch.object(MfaForcedAligner, "measure") as measure:
            timings, confidence, method, warning, reference = _align_training_phonemes(
                Path("unused.wav"), Path("."), [], 1.0, 0.0, []
            )
        measure.assert_not_called()
        self.assertEqual([], timings)
        self.assertEqual("unavailable", method)
        self.assertIsNone(reference)

    # {
    #   責務: [test_supplied_transcript_is_aligned_without_running_asr: 修正歌詞がASRで上書きされないことを確認する]
    # }
    def test_supplied_transcript_is_aligned_without_running_asr(self):
        workspace = Path.cwd() / "fictional-project"
        payload = {
            "segment_id": "example",
            "source_id": "source",
            "audio_path": str(workspace / "audio.wav"),
            "workspace_path": str(workspace),
            "cache_key": "fixture",
            "transcript_override": "修正歌詞",
        }
        with (
            patch(
                "your_singer_ml.universal_features._load_pcm16_mono",
                return_value=(48000, array.array("h", [0] * 4800)),
            ),
            patch("your_singer_ml.universal_features._transcribe") as transcribe,
            patch("your_singer_ml.universal_features._phonemize", return_value=["a"]),
            patch(
                "your_singer_ml.universal_features._align_training_phonemes",
                return_value=(
                    [],
                    0.0,
                    "fixture-method",
                    None,
                    {
                        "path": "alignment/measurements/example.json",
                        "sha256": "1" * 64,
                        "audio_sha256": "2" * 64,
                    },
                ),
            ) as align,
            patch(
                "your_singer_ml.universal_features._extract_frame_features",
                return_value=([], [], []),
            ),
            patch(
                "your_singer_ml.universal_features._write_feature",
                return_value="features/example.json",
            ),
        ):
            feature = extract_universal_features(payload)
        transcribe.assert_not_called()
        self.assertEqual("修正歌詞", feature["transcript"])
        self.assertEqual(0.0, feature["asr_confidence"])
        self.assertEqual(
            "alignment/measurements/example.json", feature["alignment_measurement"]["path"]
        )
        self.assertEqual(["a"], align.call_args.args[2])


if __name__ == "__main__":
    unittest.main()
