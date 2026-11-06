import csv
import json
import struct
import tempfile
import unittest
import wave
from pathlib import Path

from your_singer_ml import diffsinger_duration_input as duration

RATE = 1000


def write_wav(path: Path, seconds: float) -> None:
    with wave.open(str(path), "wb") as stream:
        stream.setnchannels(1)
        stream.setsampwidth(2)
        stream.setframerate(RATE)
        stream.writeframes(struct.pack("<h", 0) * round(seconds * RATE))


# {
#   責務: [DiffSingerDurationInputTests: 拍の語分割・保留理由・公式列・来歴・既存出力の拒否を確認する]
#   補足: [合成の音響学習入力で確認する。公式binarizer・実Trainerでの実行は未検証]
# }
class DiffSingerDurationInputTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.job = self.root / "training" / "diffsinger" / "job-1"
        (self.job / "acoustic-input" / "wavs").mkdir(parents=True)
        self.payload = dict(project_root=str(self.root), job_id="job-1", dataset_fingerprint="fp")

    def build(self, clips):
        # clips: name -> (phones, durations, wav_seconds)
        source = self.job / "acoustic-input"
        with (source / "transcriptions.csv").open("w", encoding="utf-8", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(["name", "ph_seq", "ph_dur"])
            for name, (phones, durations, seconds) in clips.items():
                writer.writerow([name, " ".join(phones), " ".join(str(d) for d in durations)])
                write_wav(source / "wavs" / f"{name}.wav", seconds)
        provenance = dict(
            schema="your-singer-diffsinger-acoustic-v1",
            job_id="job-1",
            dataset_fingerprint="fp",
            weight_manifest_sha256="w" * 64,
            items=[
                dict(
                    name=n,
                    segment_id="seg-" + n,
                    training_weight=1.0,
                    measurement=dict(path="m", sha256="0" * 64),
                )
                for n in clips
            ],
        )
        (source / "provenance.json").write_text(json.dumps(provenance), encoding="utf-8")

    def test_moras_group_consonant_with_vowel_and_standalone_phonemes(self):
        self.assertEqual(
            duration.group_moras(["k", "a", "N", "sh", "i", "cl", "t", "e", "a"]),
            [2, 1, 2, 1, 2, 1],
        )
        for bad in (["k"], ["k", "k", "a"], ["k", "N"], ["x"]):
            with self.assertRaises(ValueError):
                duration.group_moras(bad)

    def test_valid_clips_are_written_with_ph_num_and_provenance(self):
        self.build({"c1": (["k", "a", "N"], [0.2, 0.3, 0.5], 1.0)})
        result = duration.prepare_diffsinger_duration(self.payload)
        self.assertEqual(
            (result["item_count"], result["held_count"], result["training_run"]), (1, 0, False)
        )
        out = self.job / "duration-input"
        with (out / "transcriptions.csv").open(encoding="utf-8", newline="") as stream:
            row = next(csv.DictReader(stream))
        self.assertEqual(list(row), duration.CSV_COLUMNS)
        self.assertEqual(row["ph_num"], "2 1")
        record = json.loads((out / "provenance.json").read_text(encoding="utf-8"))
        self.assertFalse(record["word_grouping"]["measured"])
        self.assertEqual(record["items"][0]["segment_id"], "seg-c1")
        self.assertTrue((out / "wavs" / "c1.wav").is_file())

    def test_bad_clips_are_held_with_reasons_and_not_discarded_silently(self):
        self.build(
            {
                "ok": (["a"], [1.0], 1.0),
                "unknown": (["x"], [1.0], 1.0),
                "dangling": (["k"], [1.0], 1.0),
                "negative": (["a"], [-1.0], 1.0),
                "zero": (["a", "i"], [0.0, 1.0], 1.0),
                "overrun": (["a"], [1.5], 1.0),
            }
        )
        result = duration.prepare_diffsinger_duration(self.payload)
        self.assertEqual((result["item_count"], result["held_count"]), (1, 5))
        record = json.loads(
            (self.job / "duration-input" / "provenance.json").read_text(encoding="utf-8")
        )
        self.assertEqual(
            {h["name"] for h in record["held"]},
            {"unknown", "dangling", "negative", "zero", "overrun"},
        )
        self.assertTrue(all(h["reason"] for h in record["held"]))

    def test_job_mismatch_existing_output_and_no_usable_clip_fail_without_output(self):
        self.build({"bad": (["x"], [1.0], 1.0)})
        with self.assertRaises(ValueError):
            duration.prepare_diffsinger_duration(self.payload)
        self.assertFalse((self.job / "duration-input").exists())
        self.assertEqual([p.name for p in self.job.iterdir()], ["acoustic-input"])
        with self.assertRaises(ValueError):
            duration.prepare_diffsinger_duration({**self.payload, "dataset_fingerprint": "other"})
        (self.job / "duration-input").mkdir()
        with self.assertRaises(FileExistsError):
            duration.prepare_diffsinger_duration(self.payload)


if __name__ == "__main__":
    unittest.main()
