import hashlib
import json
import tempfile
import types
import unittest
from collections import Counter
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from your_singer_ml import diffsinger_training
from your_singer_ml import diffsinger_weighted_sampler as sampler


class FakeDataset:
    def __init__(self, data_dir, size):
        self.data_dir = str(data_dir)
        self.size = size

    def __len__(self):
        return self.size


# 公式の DsBatchSampler の代わり: sub_indices があればそれを、なければ全itemの順列を1つのバッチとして返す。
class FakeBase:
    def __init__(self, dataset, shuffle_sample=False, **kwargs):
        self.dataset = dataset
        self.shuffle_sample = shuffle_sample
        self.sub_indices = None
        self.epoch = 0
        self.formed = None

    def set_epoch(self, epoch):
        self.epoch = epoch

    def __iter__(self):
        return iter(
            [
                list(self.sub_indices)
                if self.sub_indices is not None
                else list(range(len(self.dataset)))
            ]
        )

    def __len__(self):
        return 1


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


# {
#   責務: [DiffSingerWeightedSamplerTests: 重みmanifestの検証・item名の記録・重み付き抽出・使用記録・学習入口の切替を確認する]
#   補足: [公式サンプラーの代わりの合成基底クラスで確認する。実際の公式binarization・実GPUでの効果は未検証]
# }
class DiffSingerWeightedSamplerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.job = Path(self.temporary.name) / "training" / "diffsinger" / "job-1"
        (self.job / "acoustic-input").mkdir(parents=True)
        self.binary = Path(self.temporary.name) / "binary"
        self.binary.mkdir()
        self.report = Path(self.temporary.name) / "weight_sampling.json"

    def write_job(self, weights, provenance_weights=None):
        manifest_items = [
            dict(segment_id=f"s{i}", audio_filename=f"s{i}.wav", training_weight=w)
            for i, w in enumerate(weights)
        ]
        manifest = self.job / "train_weights.json"
        manifest.write_text(
            json.dumps(dict(job_id="job-1", items=manifest_items)), encoding="utf-8"
        )
        digest = sha(manifest)
        (self.job / "dataset.json").write_text(
            json.dumps(
                dict(weight_manifest_file="train_weights.json", weight_manifest_sha256=digest)
            ),
            encoding="utf-8",
        )
        values = provenance_weights if provenance_weights is not None else weights
        provenance = dict(
            weight_manifest_sha256=digest,
            items=[
                dict(name=f"segment_{i}", segment_id=f"s{i}", training_weight=w)
                for i, w in enumerate(values)
            ],
        )
        (self.job / "acoustic-input" / "provenance.json").write_text(
            json.dumps(provenance), encoding="utf-8"
        )
        return digest

    def write_names(self, count):
        (self.binary / sampler.NAMES_FILE).write_text(
            json.dumps(dict(names=[f"segment_{i}" for i in range(count)])), encoding="utf-8"
        )

    def test_weights_are_loaded_only_when_manifest_provenance_and_range_agree(self):
        digest = self.write_job([0.5, 2.0])
        weights, actual = sampler.load_name_weights(self.job)
        self.assertEqual((weights, actual), ({"segment_0": 0.5, "segment_1": 2.0}, digest))
        self.write_job([0.5, 2.0], provenance_weights=[0.5, 1.0])
        with self.assertRaises(ValueError):
            sampler.load_name_weights(self.job)
        self.write_job([0.5, 3.0])
        with self.assertRaises(ValueError):
            sampler.load_name_weights(self.job)
        self.write_job([0.5, 2.0])
        (self.job / "train_weights.json").write_text("{}", encoding="utf-8")
        with self.assertRaises(ValueError):
            sampler.load_name_weights(self.job)

    def test_a_job_without_a_manifest_record_is_not_silently_unweighted(self):
        (self.job / "dataset.json").write_text("{}", encoding="utf-8")
        with self.assertRaises(ValueError):
            sampler.load_name_weights(self.job)

    def test_name_recorder_saves_train_item_names_in_item_order(self):
        class Builder:
            def __init__(self, path, prefix):
                self.path = Path(path) / f"{prefix}.data"
                self.prefix = prefix
                self.counter = 0

            def add_item(self, item, item_no=None):
                number = self.counter
                self.counter += 1
                return number

            def finalize(self):
                return None

        module = types.SimpleNamespace(IndexedDatasetBuilder=Builder)
        sampler.install_name_recorder(module)
        train, valid = Builder(self.binary, "train"), Builder(self.binary, "valid")
        for name in ("0:segment_a", "0:segment_b"):
            train.add_item({"name": name})
        valid.add_item({"name": "0:segment_v"})
        train.finalize()
        valid.finalize()
        self.assertEqual(sampler.read_item_names(self.binary), ["segment_a", "segment_b"])
        with self.assertRaises(ValueError):
            sampler.read_item_names(self.binary / "missing")

    def make(self, weights, seed=1, **kwargs):
        self.write_names(len(weights))
        mapping = {f"segment_{i}": w for i, w in enumerate(weights)}
        cls = sampler.make_weighted_sampler_class(FakeBase, mapping, seed, "h" * 64, self.report)
        return cls(FakeDataset(self.binary, len(weights)), **kwargs)

    def draws(self, instance, epochs=300):
        counts = Counter()
        for epoch in range(epochs):
            instance.set_epoch(epoch)
            counts.update(next(iter(instance)))
        return counts

    def test_frequency_follows_weights_and_reverses_with_them(self):
        high = [2.0 if i % 2 == 0 else 0.5 for i in range(20)]
        low = [0.5 if i % 2 == 0 else 2.0 for i in range(20)]
        forward = self.draws(self.make(high, shuffle_sample=True))
        reverse = self.draws(self.make(low, shuffle_sample=True))
        even = sum(forward[i] for i in range(0, 20, 2)) / sum(forward[i] for i in range(1, 20, 2))
        odd = sum(reverse[i] for i in range(0, 20, 2)) / sum(reverse[i] for i in range(1, 20, 2))
        self.assertGreater(even, 3.0)
        self.assertLess(odd, 0.34)

    def test_same_seed_and_epoch_reproduce_and_epochs_differ(self):
        weights = [0.5 + (i % 4) * 0.5 for i in range(30)]
        first, second = (
            self.make(weights, 5, shuffle_sample=True),
            self.make(weights, 5, shuffle_sample=True),
        )
        other = self.make(weights, 6, shuffle_sample=True)
        for instance in (first, second, other):
            instance.set_epoch(3)
        self.assertEqual(list(first), list(second))
        self.assertNotEqual(list(first), list(other))
        first.set_epoch(4)
        self.assertNotEqual(list(first), list(second))

    def test_the_validation_sampler_is_left_as_the_official_one(self):
        instance = self.make([0.5, 2.0], shuffle_sample=False)
        self.assertEqual(list(instance), [[0, 1]])
        self.assertFalse(self.report.exists())

    def test_missing_names_or_weights_fail_instead_of_using_equal_weights(self):
        mapping = {"segment_0": 1.0}
        cls = sampler.make_weighted_sampler_class(FakeBase, mapping, 1, "h" * 64, self.report)
        with self.assertRaises(ValueError):
            cls(FakeDataset(self.binary, 2), shuffle_sample=True)
        self.write_names(2)
        with self.assertRaises(ValueError):
            cls(FakeDataset(self.binary, 2), shuffle_sample=True)
        with self.assertRaises(ValueError):
            cls(FakeDataset(self.binary, 3), shuffle_sample=True)

    def test_the_usage_report_proves_the_sampler_ran(self):
        instance = self.make([0.5, 2.0], shuffle_sample=True)
        with self.assertRaises(RuntimeError):
            sampler.read_report(self.report)
        instance.set_epoch(2)
        list(instance)
        used = sampler.read_report(self.report)
        self.assertEqual(
            (used["sampler_version"], used["item_count"], used["last_epoch"]),
            (sampler.SAMPLER_VERSION, 2, 2),
        )
        self.assertEqual((used["min_weight"], used["max_weight"]), (0.5, 2.0))

    def test_install_sampler_replaces_the_official_class_in_base_task(self):
        module = types.SimpleNamespace(DsBatchSampler=FakeBase)
        sampler.install_sampler(module, {"segment_0": 1.0}, 1, "h" * 64, self.report)
        self.assertTrue(issubclass(module.DsBatchSampler, FakeBase))
        self.assertIsNot(module.DsBatchSampler, FakeBase)

    def trainer_layout(self):
        root = Path(self.temporary.name) / "bundle"
        (root / "runtime").mkdir(parents=True)
        (root / "runtime" / "python.exe").write_bytes(b"py")
        (root / "source" / "scripts").mkdir(parents=True)
        (root / "source" / "scripts" / "train.py").write_bytes(b"entry")
        config = Path(self.temporary.name) / "acoustic.yaml"
        config.write_text("base_config: []\n", encoding="utf-8")
        return root, config

    def test_training_starts_through_the_weighted_entry_and_requires_the_usage_report(self):
        self.write_job([0.5, 2.0])
        root, config = self.trainer_layout()
        output = self.job / "trainer"
        payload = dict(
            trainer_root=str(root),
            config_path=str(config),
            exp_name="job-1",
            output_dir=str(output),
            seed=4,
        )
        commands = []

        def fake_run(command, **kwargs):
            commands.append(command)
            return SimpleNamespace(returncode=0)

        with patch("your_singer_ml.diffsinger_training.run_reclaiming", side_effect=fake_run):
            with self.assertRaises(RuntimeError):
                diffsinger_training.train_diffsinger(payload)
        command = commands[0]
        self.assertTrue(command[1].endswith("diffsinger_train_entry.py"))
        self.assertEqual(command[3:7], [str(self.job), "4", "--", "--config"])

        output.joinpath("weight_sampling.json").write_text(
            json.dumps(dict(applied=True, iterations=2, sampler_version=sampler.SAMPLER_VERSION)),
            encoding="utf-8",
        )
        with patch("your_singer_ml.diffsinger_training.run_reclaiming", side_effect=fake_run):
            result = diffsinger_training.train_diffsinger(payload)
        self.assertEqual(result["weight_sampling"]["iterations"], 2)

    def test_training_without_a_manifest_keeps_the_official_entry(self):
        root, config = self.trainer_layout()
        output = Path(self.temporary.name) / "plain" / "trainer"
        payload = dict(
            trainer_root=str(root),
            config_path=str(config),
            exp_name="job-1",
            output_dir=str(output),
        )
        commands = []

        def fake_run(command, **kwargs):
            commands.append(command)
            return SimpleNamespace(returncode=0)

        with patch("your_singer_ml.diffsinger_training.run_reclaiming", side_effect=fake_run):
            result = diffsinger_training.train_diffsinger(payload)
        self.assertTrue(commands[0][1].endswith("train.py"))
        self.assertNotIn("weight_sampling", result)


if __name__ == "__main__":
    unittest.main()
