import copy
import csv
import hashlib
import json
import os
import sys
import tempfile
import unittest
import wave
from unittest.mock import patch
from pathlib import Path

from your_singer_ml.diffsinger_acoustic_input import prepare_diffsinger_acoustic
from your_singer_ml.verified_file_source import open_verified_file


# { 責務: [DiffSingerAcousticInputTests: 合成PCMで公式入力変換の境界と失敗時の保存状態を確認する] }
class DiffSingerAcousticInputTests(unittest.TestCase):
    # { 責務: [setUp: 非公開素材を含まない合成PCMとjob情報を一時領域へ用意する] }
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.project = Path(self.temporary.name)
        self.dataset = self.project / "training/diffsinger/test-job"
        self.dataset.mkdir(parents=True)
        audio = self.project / "audio.wav"
        with wave.open(str(audio), "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(1000)
            wav.writeframes(b"\x01\x00" * 1000)
        record = self.project / "measurement.json"
        record.write_text("{}", encoding="utf-8")
        (self.project / "f0.json").write_text("[]")
        (self.project / "energy.json").write_text("[]")
        self.payload = dict(
            project_root=str(self.project),
            job_id="test-job",
            speaker_id="speaker",
            dataset_fingerprint="fingerprint",
            auto_correction_enabled=False,
        )
        self.document = {key: value for key, value in self.payload.items() if key != "project_root"}
        self.document.update(
            stage_version="v1-10.5-mfa-measurement",
            items=[
                dict(
                    segment_id="../../private-segment",
                    audio_path=str(audio),
                    alignment_measurement={
                        "audio_sha256": hashlib.sha256(audio.read_bytes()).hexdigest(),
                        "path": "measurement.json",
                        "sha256": hashlib.sha256(record.read_bytes()).hexdigest(),
                    },
                    phonemes=["a", "i"],
                    alignment_method="mfa-japanese-phone-forced-alignment-v1",
                    phoneme_starts_sec=[0.2, 0.4],
                    phoneme_ends_sec=[0.4, 0.7],
                    durations_sec=[0.2, 0.3],
                    f0_feature_path="f0.json",
                    energy_feature_path="energy.json",
                )
            ],
        )

    # { 責務: [test_windows_short_name: 短縮名で同じ通常ファイルを読めることを確認する] }
    @unittest.skipUnless(sys.platform == "win32", "Windowsの短縮名を確認する")
    def test_windows_short_name(self):
        import ctypes
        from ctypes import wintypes

        path = self.project / "audio.wav"
        query = ctypes.WinDLL("kernel32", use_last_error=True).GetShortPathNameW
        query.argtypes = (wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD)
        query.restype = wintypes.DWORD
        buffer = ctypes.create_unicode_buffer(32768)
        length = query(str(path), buffer, len(buffer))
        if not length or length >= len(buffer) or buffer.value.lower() == str(path).lower():
            self.skipTest("このファイルシステムでは短縮名を利用できません。")
        with open_verified_file(Path(buffer.value)) as stream:
            self.assertEqual(stream.read(4), b"RIFF")

    # { 責務: [save: 試験対象の入力manifestを書き出す] }
    def save(self):
        data = json.dumps(self.document).encode("utf-8")
        (self.dataset / "dataset.json").write_bytes(data)
        self.payload["dataset_sha256"] = hashlib.sha256(data).hexdigest()

    # { 責務: [assert_rejected: 入力拒否時に完成品や一時ファイルが残らないことを確認する] }
    def assert_rejected(self, message):
        self.save()
        with self.assertRaisesRegex(ValueError, message):
            prepare_diffsinger_acoustic(self.payload)
        self.assertFalse((self.dataset / "acoustic-input").exists())
        self.assertFalse(list(self.dataset.glob(".acoustic-*")))

    # { 責務: [test_crops_audio_to_measured_extent_and_preserves_phone_durations: 実測範囲のPCMと音素長を照合する] }
    def test_crops_audio_to_measured_extent_and_preserves_phone_durations(self):
        self.save()
        result = prepare_diffsinger_acoustic(self.payload)
        raw = Path(result["raw_data_dir"])
        with (raw / "transcriptions.csv").open(encoding="utf-8", newline="") as stream:
            rows = list(csv.DictReader(stream))
        self.assertEqual(rows[0]["ph_seq"], "a i")
        self.assertEqual(rows[0]["ph_dur"], "0.2 0.3")
        with wave.open(str(raw / "wavs" / (rows[0]["name"] + ".wav"))) as audio:
            self.assertEqual(audio.getnframes(), 500)
            self.assertEqual(audio.readframes(500), b"\x01\x00" * 500)
        provenance = json.loads((raw / "provenance.json").read_text(encoding="utf-8"))
        self.assertEqual(provenance["items"][0]["crop"]["start_sample"], 200)
        self.assertFalse(result["training_run"])
        self.assertEqual((raw / provenance["items"][0]["measurement"]["path"]).read_text(), "{}")
        self.assertEqual((raw / "dictionary-ja.txt").read_text(), "a\ta\ni\ti\n")
        with self.assertRaises(FileExistsError):
            prepare_diffsinger_acoustic(self.payload)

    # { 責務: [test_long_audio_is_copied_in_multiple_blocks: 長い音声も元のPCMと一致させる] }
    def test_long_audio_is_copied_in_multiple_blocks(self):
        audio = self.project / "audio.wav"
        with wave.open(str(audio), "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(1000)
            wav.writeframes(b"\x01\x00" * 140000)
        item = self.document["items"][0]
        item["alignment_measurement"]["audio_sha256"] = hashlib.sha256(
            audio.read_bytes()
        ).hexdigest()
        item["phoneme_starts_sec"] = [0.0, 70.0]
        item["phoneme_ends_sec"] = [70.0, 130.0]
        item["durations_sec"] = [70.0, 60.0]
        self.save()
        raw = Path(prepare_diffsinger_acoustic(self.payload)["raw_data_dir"])
        clip = next((raw / "wavs").glob("*.wav"))
        with wave.open(str(clip), "rb") as wav:
            self.assertEqual(wav.getnframes(), 130000)
            self.assertEqual(wav.readframes(130000), b"\x01\x00" * 130000)

    # { 責務: [test_dataset_changed_after_validation_is_rejected: 検証後に変更されたdatasetを拒否する] }
    def test_dataset_changed_after_validation_is_rejected(self):
        self.save()
        (self.dataset / "dataset.json").write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "変更"):
            prepare_diffsinger_acoustic(self.payload)
        self.assertFalse((self.dataset / "acoustic-input").exists())

    # { 責務: [test_audio_changed_after_validation_is_rejected: 測定後に変更された音声を拒否する] }
    def test_audio_changed_after_validation_is_rejected(self):
        self.save()
        (self.project / "audio.wav").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "変更"):
            prepare_diffsinger_acoustic(self.payload)
        self.assertFalse((self.dataset / "acoustic-input").exists())
        self.assertFalse(list(self.dataset.glob(".acoustic-*")))

    # { 責務: [test_opened_audio_must_match_checked_path: 開いたファイルの実体が違う場合に拒否する] }
    def test_opened_audio_must_match_checked_path(self):
        expected = self.project / "audio.wav"
        other = self.project / "other.wav"
        other.write_bytes(b"other")
        descriptor = os.open(other, os.O_RDONLY | getattr(os, "O_BINARY", 0))
        with patch("your_singer_ml.verified_file_source.os.open", return_value=descriptor):
            with self.assertRaisesRegex(ValueError, "差し替え"):
                open_verified_file(expected)

    # { 責務: [test_fifo_does_not_block: LinuxのFIFOは待機せず通常ファイルではないとして拒否する] }
    @unittest.skipUnless(sys.platform.startswith("linux"), "LinuxのFIFOを確認する")
    def test_fifo_does_not_block(self):
        fifo = self.project / "input-fifo"
        os.mkfifo(fifo)
        with self.assertRaisesRegex(ValueError, "通常ファイル"):
            open_verified_file(fifo)

    # { 責務: [test_dataset_hash_is_required: 照合用ハッシュの欠落を拒否する] }
    def test_dataset_hash_is_required(self):
        self.save()
        del self.payload["dataset_sha256"]
        with self.assertRaisesRegex(ValueError, "変更"):
            prepare_diffsinger_acoustic(self.payload)

    # { 責務: [test_measurement_changed_after_validation_is_rejected: 検証後の測定記録の変更を拒否する] }
    def test_measurement_changed_after_validation_is_rejected(self):
        self.save()
        (self.project / "measurement.json").write_text("changed", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "変更"):
            prepare_diffsinger_acoustic(self.payload)
        self.assertFalse((self.dataset / "acoustic-input").exists())
        self.assertFalse(list(self.dataset.glob(".acoustic-*")))

    # { 責務: [test_absolute_measurement_path_is_rejected: 測定記録はproject相対パスを要求する] }
    def test_absolute_measurement_path_is_rejected(self):
        self.document["items"][0]["alignment_measurement"]["path"] = str(
            self.project / "measurement.json"
        )
        self.assert_rejected("参照")

    # { 責務: [test_old_input_without_absolute_boundaries_is_rejected: 境界を持たない旧形式を拒否する] }
    def test_old_input_without_absolute_boundaries_is_rejected(self):
        self.document["stage_version"] = "v1-10.3"
        self.assert_rejected("再生成")

    # { 責務: [test_job_fingerprint_mismatch_is_rejected: 別jobの入力を拒否する] }
    def test_job_fingerprint_mismatch_is_rejected(self):
        self.document["dataset_fingerprint"] = "different"
        self.assert_rejected("一致しません")

    # { 責務: [test_unknown_gap_is_not_labelled_as_silence: 隙間を推定で埋めないことを確認する] }
    def test_unknown_gap_is_not_labelled_as_silence(self):
        self.document["items"][0]["phoneme_starts_sec"][1] = 0.5
        self.document["items"][0]["durations_sec"][1] = 0.2
        self.assert_rejected("隙間")

    # { 責務: [test_inconsistent_duration_is_rejected: 音素長と境界の不一致を拒否する] }
    def test_inconsistent_duration_is_rejected(self):
        self.document["items"][0]["durations_sec"][0] = 0.3
        self.assert_rejected("一致していません")

    # { 責務: [test_duplicate_segment_is_rejected: 同じsegmentの重複を拒否する] }
    def test_duplicate_segment_is_rejected(self):
        self.document["items"].append(copy.deepcopy(self.document["items"][0]))
        self.assert_rejected("重複")

    # { 責務: [test_nonfinite_time_is_rejected: 非有限な時刻を拒否する] }
    def test_nonfinite_time_is_rejected(self):
        self.document["items"][0]["phoneme_ends_sec"][1] = float("nan")
        self.assert_rejected("有限")

    # { 責務: [test_audio_symlink_inside_project_is_rejected: 同じ領域内を指すリンクも拒否する] }
    def test_audio_symlink_inside_project_is_rejected(self):
        link = self.project / "linked.wav"
        try:
            link.symlink_to(self.project / "audio.wav")
        except (OSError, NotImplementedError):
            self.skipTest("この環境ではシンボリックリンクを作成できません")
        self.document["items"][0]["audio_path"] = str(link)
        self.assert_rejected("リンク")

    # { 責務: [test_audio_outside_project_is_rejected: 作業領域外の素材を拒否する] }
    def test_audio_outside_project_is_rejected(self):
        self.document["items"][0]["audio_path"] = str(Path(__file__).resolve())
        self.assert_rejected("領域外")

    # { 責務: [test_out_of_range_audio_boundary_does_not_leave_partial_input: 切り出し失敗時の残骸を防ぐ] }
    def test_out_of_range_audio_boundary_does_not_leave_partial_input(self):
        self.document["items"][0]["phoneme_ends_sec"][1] = 2.0
        self.document["items"][0]["durations_sec"][1] = 1.6
        self.assert_rejected("音声範囲外")


if __name__ == "__main__":
    unittest.main()
