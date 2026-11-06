import hashlib
import json
import tempfile
import types
import unittest
from collections import Counter
from pathlib import Path
from unittest.mock import patch

from your_singer_ml import style_bert_vits2_weighted_sampler as sampler
from your_singer_ml.style_bert_vits2_session import StyleBertVits2TrainingSession


class FakeDataset:
    def __init__(self, names):
        self.audiopaths_sid_text = [[f"data/wavs/{name}", "spk", "JP", "text"] for name in names]
        self.lengths = [100] * len(names)


# 公式の DistributedBucketSampler の代わり: buckets の中身をそのまま1列にして返す(重みの効果だけを観察する)。
class FakeBase:
    def __init__(self, dataset, batch_size, boundaries, num_replicas=1, rank=0, shuffle=True):
        self.buckets = [list(range(len(dataset.audiopaths_sid_text)))]
        self.epoch = 0

    def set_epoch(self, epoch):
        self.epoch = epoch

    def __iter__(self):
        return iter([list(bucket) for bucket in self.buckets])


def write_manifest(path, job_id, items):
    path.write_text(
        json.dumps(
            dict(
                schema_version="1",
                job_id=job_id,
                policy_version="v1",
                input_fingerprint="x",
                items=[
                    dict(segment_id=f"s{i}", audio_filename=name, training_weight=weight)
                    for i, (name, weight) in enumerate(items)
                ],
                skipped=[],
            )
        ),
        encoding="utf-8",
    )
    return hashlib.sha256(path.read_bytes()).hexdigest()


# {
#   責務: [StyleBertVits2WeightedSamplerTests: 重み付きサンプリングの検証・差し替え・方向・再現性・使用記録を確認する]
#   補足: [公式サンプラーの代わりの合成基底クラスで確認する。実際の学習での効果(実GPU)は未検証]
# }
class StyleBertVits2WeightedSamplerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.manifest = self.root / "train_weights.json"

    def test_manifest_is_verified_by_hash_job_range_and_duplicates(self):
        digest = write_manifest(self.manifest, "job", [("a.wav", 0.5), ("b.wav", 2.0)])
        weights, actual = sampler.load_weight_manifest(self.manifest, digest, "job")
        self.assertEqual(weights, {"a.wav": 0.5, "b.wav": 2.0})
        self.assertEqual(actual, digest)
        with self.assertRaises(ValueError):
            sampler.load_weight_manifest(self.manifest, "0" * 64, "job")
        with self.assertRaises(ValueError):
            sampler.load_weight_manifest(self.manifest, digest, "other-job")
        for bad in ([("a.wav", 0.1)], [("a.wav", 3.0)], [("a.wav", 1.0), ("a.wav", 1.0)], []):
            bad_digest = write_manifest(self.manifest, "job", bad)
            with self.assertRaises(ValueError):
                sampler.load_weight_manifest(self.manifest, bad_digest, "job")

    def make(self, weights, seed=1, names=("a.wav", "b.wav")):
        report = self.root / "weight_sampling.json"
        cls = sampler.make_weighted_sampler_class(FakeBase, weights, seed, "h" * 64, report)
        return cls(FakeDataset(names), 2, [32, 300]), report

    def frequencies(self, instance, epochs=400):
        counts = Counter()
        for epoch in range(epochs):
            instance.set_epoch(epoch)
            counts.update(
                next(iter(instance)) if False else [i for batch in instance for i in batch]
            )
        return counts

    def test_sampling_frequency_follows_the_weights_and_reverses_with_them(self):
        names = [f"w{i}.wav" for i in range(20)]
        high = {name: (2.0 if i % 2 == 0 else 0.5) for i, name in enumerate(names)}
        low = {name: (0.5 if i % 2 == 0 else 2.0) for i, name in enumerate(names)}
        forward, _ = self.make(high, names=names)
        reverse, _ = self.make(low, names=names)
        f, r = self.frequencies(forward), self.frequencies(reverse)
        even = sum(f[i] for i in range(0, 20, 2)) / sum(f[i] for i in range(1, 20, 2))
        odd = sum(r[i] for i in range(0, 20, 2)) / sum(r[i] for i in range(1, 20, 2))
        self.assertGreater(even, 3.0)
        self.assertLess(odd, 0.34)

    def test_same_seed_and_epoch_are_reproducible_and_epochs_differ(self):
        names = [f"w{i}.wav" for i in range(30)]
        weights = {name: 0.5 + (i % 4) * 0.5 for i, name in enumerate(names)}
        first, _ = self.make(weights, seed=5, names=names)
        second, _ = self.make(weights, seed=5, names=names)
        other, _ = self.make(weights, seed=6, names=names)
        for instance in (first, second, other):
            instance.set_epoch(3)
        self.assertEqual(list(first), list(second))
        self.assertNotEqual(list(first), list(other))
        first.set_epoch(4)
        self.assertNotEqual(list(first), list(second))

    def test_bucket_sizes_are_kept_and_the_buckets_are_restored(self):
        weights = {"a.wav": 2.0, "b.wav": 0.5}
        instance, _ = self.make(weights)
        before = [list(bucket) for bucket in instance.buckets]
        batches = list(instance)
        self.assertEqual(sum(len(batch) for batch in batches), 2)
        self.assertEqual(instance.buckets, before)

    def test_an_item_without_a_weight_fails_instead_of_using_equal_weights(self):
        with self.assertRaises(ValueError):
            self.make({"a.wav": 1.0})

    def test_the_usage_report_proves_the_sampler_ran(self):
        instance, report = self.make({"a.wav": 2.0, "b.wav": 0.5})
        with self.assertRaises(RuntimeError):
            sampler.read_report(report)
        instance.set_epoch(2)
        list(instance)
        used = sampler.read_report(report)
        self.assertEqual(used["sampler_version"], sampler.SAMPLER_VERSION)
        self.assertEqual(
            (used["min_weight"], used["max_weight"], used["item_count"]), (0.5, 2.0, 2)
        )
        self.assertEqual(used["last_epoch"], 2)
        with self.assertRaises(RuntimeError):
            sampler.read_report(self.root / "missing.json")

    def test_install_replaces_the_official_sampler(self):
        module = types.SimpleNamespace(DistributedBucketSampler=FakeBase)
        sampler.install(module, {"a.wav": 1.0}, 1, "h" * 64, self.root / "r.json")
        self.assertTrue(issubclass(module.DistributedBucketSampler, FakeBase))
        self.assertIsNot(module.DistributedBucketSampler, FakeBase)


# {
#   責務: [SessionWeightManifestTests: 学習起動前に、重みmanifestと学習itemが1対1で一致することを確認する]
# }
class SessionWeightManifestTests(unittest.TestCase):
    def setUp(self):
        check = patch("your_singer_ml.style_bert_vits2_session.StyleBertVits2Bundle.validate")
        check.start()
        self.addCleanup(check.stop)
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.trainer, self.dataset = root / "bundle", root / "job"
        for relative in (
            "runtime/python.exe",
            "resample.py",
            "preprocess_text.py",
            "bert_gen.py",
            "style_gen.py",
            "train_ms.py",
            "default_config.yml",
            "configs/config.json",
            "style_bert_vits2/constants.py",
            "pretrained/G_0.safetensors",
            "pretrained/D_0.safetensors",
            "pretrained/DUR_0.safetensors",
        ):
            path = self.trainer / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"resource")
        (self.dataset / "raw").mkdir(parents=True)
        self.audio = self.dataset / "raw" / "a.wav"
        self.audio.write_bytes(b"audio")
        (self.dataset / "esd.list").write_text("a.wav|話者|JP|テスト\n", encoding="utf-8")
        self.payload = dict(
            trainer_root=str(self.trainer),
            dataset_dir=str(self.dataset),
            output_dir=str(self.dataset / "trainer"),
            model_name="話者モデル",
            job_id="job",
            dataset_fingerprint="fp",
            auto_correction_enabled=False,
            seed=9,
        )

    def record(self, **extra):
        record = dict(
            job_id="job",
            dataset_fingerprint="fp",
            auto_correction_enabled=False,
            items=[dict(audio_path=str(self.audio), speaker_name="話者", transcript="テスト")],
            **extra,
        )
        (self.dataset / "dataset.json").write_text(
            json.dumps(record, ensure_ascii=False), encoding="utf-8"
        )

    def test_matching_manifest_is_copied_and_recorded_for_the_trainer(self):
        digest = write_manifest(self.dataset / "train_weights.json", "job", [("a.wav", 2.0)])
        self.record(weight_manifest_file="train_weights.json", weight_manifest_sha256=digest)
        session = StyleBertVits2TrainingSession(self.payload)
        session.prepare()
        request = json.loads((session.output / "request.json").read_text(encoding="utf-8"))
        self.assertEqual(
            request["weight_manifest"], dict(file="train_weights.json", sha256=digest, seed=9)
        )
        self.assertTrue((session.data / "train_weights.json").is_file())

    def test_tampered_extra_or_missing_weights_fail_before_any_output(self):
        digest = write_manifest(self.dataset / "train_weights.json", "job", [("a.wav", 2.0)])
        self.record(weight_manifest_file="train_weights.json", weight_manifest_sha256="0" * 64)
        with self.assertRaises(ValueError):
            StyleBertVits2TrainingSession(self.payload).prepare()
        extra = write_manifest(
            self.dataset / "train_weights.json", "job", [("a.wav", 2.0), ("b.wav", 1.0)]
        )
        self.record(weight_manifest_file="train_weights.json", weight_manifest_sha256=extra)
        with self.assertRaises(ValueError):
            StyleBertVits2TrainingSession(self.payload).prepare()
        self.assertFalse((self.dataset / "trainer").exists())
        self.assertNotEqual(digest, extra)

    def test_legacy_input_without_a_manifest_is_reported_as_unweighted(self):
        self.record()
        session = StyleBertVits2TrainingSession(self.payload)
        session.prepare()
        self.assertIsNone(session.weight_manifest)
        request = json.loads((session.output / "request.json").read_text(encoding="utf-8"))
        self.assertIsNone(request["weight_manifest"])


if __name__ == "__main__":
    unittest.main()
