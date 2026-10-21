import copy
import csv
import json
import tempfile
import unittest
import wave
from pathlib import Path

from your_singer_ml.diffsinger_acoustic_input import prepare_diffsinger_acoustic


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
        (self.project / "f0.json").write_text("[]")
        (self.project / "energy.json").write_text("[]")
        self.payload = dict(project_root=str(self.project), job_id="test-job", speaker_id="speaker",
                            dataset_fingerprint="fingerprint", auto_correction_enabled=False)
        self.document = {key: value for key, value in self.payload.items() if key != "project_root"}
        self.document.update(stage_version="v1-10.4", items=[dict(
            segment_id="../../private-segment", audio_path=str(audio), phonemes=["a", "i"],
            alignment_method="mfa-japanese-phone-forced-alignment-v1",
            phoneme_starts_sec=[0.2, 0.4], phoneme_ends_sec=[0.4, 0.7], durations_sec=[0.2, 0.3],
            f0_feature_path="f0.json", energy_feature_path="energy.json")])

    # { 責務: [save: 試験対象の入力manifestを書き出す] }
    def save(self):
        (self.dataset / "dataset.json").write_text(json.dumps(self.document), encoding="utf-8")

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
        self.assertEqual((raw / "dictionary-ja.txt").read_text(), "a\ta\ni\ti\n")
        with self.assertRaises(FileExistsError):
            prepare_diffsinger_acoustic(self.payload)

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
