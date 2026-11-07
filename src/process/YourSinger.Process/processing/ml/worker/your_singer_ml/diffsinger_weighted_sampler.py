from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path

SAMPLER_VERSION = "weighted-ds-batch-v1"
NAMES_FILE = "train.names.json"
MIN_WEIGHT = 0.25
MAX_WEIGHT = 2.0


# 専用Python(パッケージ外)から読み込まれるため、相対importを使わない。
def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


# {
#   責務: [load_name_weights: 歌唱jobの重みmanifestを検証し、公式入力のitem名→weightの対応を返す]
#   処理: [
#     1: dataset.json に記録されたsha256と train_weights.json を照合する(改竄を拒否)
#     2: acoustic-input/provenance.json の各クリップのweightとmanifestが一致することを確認する
#   ]
#   引数: [job_dir: training/diffsinger/<job-id>]
#   戻り値: [(name→weight, manifestのsha256)]
#   エラー: [記録・manifest・provenance の欠落、hash不一致、範囲外/非有限のweight、不一致で ValueError]
# }
def load_name_weights(job_dir: Path) -> tuple[dict[str, float], str]:
    dataset = json.loads((job_dir / "dataset.json").read_text(encoding="utf-8-sig"))
    name, digest = dataset.get("weight_manifest_file"), dataset.get("weight_manifest_sha256")
    if not isinstance(name, str) or Path(name).name != name or not isinstance(digest, str):
        raise ValueError("歌唱学習入力に重みmanifestの記録がありません(等重みで続行しません)。")
    manifest_path = job_dir / name
    if not manifest_path.is_file() or _sha256(manifest_path) != digest.lower():
        raise ValueError("重みmanifestが無い、またはsha256が学習入力の記録と一致しません。")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    by_segment = {
        entry["segment_id"]: float(entry["training_weight"]) for entry in manifest["items"]
    }
    if len(by_segment) != len(manifest["items"]):
        raise ValueError("重みmanifestに同じ区間が重複しています。")
    provenance = json.loads(
        (job_dir / "acoustic-input" / "provenance.json").read_text(encoding="utf-8")
    )
    if provenance.get("weight_manifest_sha256", "").lower() != digest.lower():
        raise ValueError("音響入力の由来が、別の重みmanifestを指しています。")
    weights: dict[str, float] = {}
    for item in provenance["items"]:
        weight = item.get("training_weight")
        if (
            not isinstance(weight, (int, float))
            or not math.isfinite(weight)
            or not MIN_WEIGHT <= weight <= MAX_WEIGHT
            or by_segment.get(item["segment_id"]) != float(weight)
            or item["name"] in weights
        ):
            raise ValueError(
                f"区間 {item.get('segment_id')} の重みが不正、または manifest と一致しません。"
            )
        weights[item["name"]] = float(weight)
    if len(weights) != len(by_segment):
        raise ValueError("音響入力のクリップと重みmanifestが1対1で一致しません。")
    return weights, digest.lower()


# {
#   責務: [read_item_names: 公式binarizationが記録した、学習データのitem番号→名前の対応を読む]
#   戻り値: [names: item番号順の名前]
#   エラー: [記録が無い(binarizationが記録パッチなしで実行された)ときValueError。名前なしで重みを推測しない]
# }
def read_item_names(binary_data_dir: Path) -> list[str]:
    path = binary_data_dir / NAMES_FILE
    if not path.is_file():
        raise ValueError(
            f"学習データのitem名の記録({NAMES_FILE})がありません。重みとitemを結び付けられないため続行しません。"
        )
    return list(json.loads(path.read_text(encoding="utf-8"))["names"])


# {
#   責務: [install_name_recorder: 公式binarizationのitem追加をラップし、学習データのitem番号→名前を保存する]
#   補足: [公式の train.meta は学習データの名前を捨てる(base_binarizer.py:354-357)ため、追加時に名前を記録する。binarization実行前に呼ぶ]
# }
def install_name_recorder(indexed_datasets) -> None:
    builder_class = indexed_datasets.IndexedDatasetBuilder
    original_add, original_finalize = builder_class.add_item, builder_class.finalize

    def add_item(self, item, item_no=None):
        number = original_add(self, item, item_no)
        if self.prefix == "train":
            # 公式の _item['name'].split(':', 1)[-1] と同じ名前(複数データセットの接頭辞を除く)。
            names = self.__dict__.setdefault("_recorded_names", {})
            names[number] = str(item["name"]).split(":", 1)[-1]
        return number

    def finalize(self):
        if self.prefix == "train" and "_recorded_names" in self.__dict__:
            names = self._recorded_names
            ordered = [names[i] for i in range(len(names))]
            (self.path.parent / NAMES_FILE).write_text(
                json.dumps({"names": ordered}, ensure_ascii=False), encoding="utf-8"
            )
        return original_finalize(self)

    builder_class.add_item, builder_class.finalize = add_item, finalize


# {
#   責務: [make_weighted_sampler_class: 公式の DsBatchSampler を、エポックごとに重みで再抽出する派生クラスにする]
#   処理: [
#     1: 学習用(shuffle_sample=True)のときだけ、全itemがweightを持つことを1対1で照合する(欠落は失敗)。検証用は公式のまま
#     2: 各エポックで (seed, epoch) 固定の乱数により、全itemの数だけ重み比例に復元抽出して sub_indices にする
#     3: 公式のバッチ形成(サイズ順・分割・DDP割当)へ渡す。使用状況をreport_pathへ書く
#   ]
# }
def make_weighted_sampler_class(
    base, name_weights: dict, seed: int, manifest_sha256: str, report_path: Path
):
    import numpy as np

    class WeightedDsBatchSampler(base):
        def __init__(self, dataset, *args, **kwargs):
            super().__init__(dataset, *args, **kwargs)
            self._weighted = bool(kwargs.get("shuffle_sample", False))
            self._drawn_epoch = None
            self.iterations = 0
            if not self._weighted:
                return
            names = read_item_names(Path(dataset.data_dir))
            if len(names) != len(dataset):
                raise ValueError("学習データのitem数と、記録されたitem名の数が一致しません。")
            missing = [name for name in names if name not in name_weights]
            if missing:
                raise ValueError(
                    f"重みの無い学習itemがあります(等重みで続行しません): {missing[:3]}"
                )
            self.item_weights = np.array([name_weights[name] for name in names], dtype=float)
            self._write_report(None)

        def _draw(self):
            if not self._weighted or self._drawn_epoch == self.epoch:
                return
            rng = np.random.default_rng([seed, self.epoch])
            probabilities = self.item_weights / self.item_weights.sum()
            self.sub_indices = rng.choice(
                len(self.item_weights), size=len(self.item_weights), p=probabilities
            ).tolist()
            self._drawn_epoch = self.epoch
            self.formed = None  # 公式のバッチ形成をこのエポックの抽出で作り直す
            self.iterations += 1
            self._write_report(self.epoch)

        def __iter__(self):
            self._draw()
            return super().__iter__()

        def __len__(self):
            self._draw()
            return super().__len__()

        def _write_report(self, epoch):
            report_path.write_text(
                json.dumps(
                    dict(
                        sampler_version=SAMPLER_VERSION,
                        applied=True,
                        seed=seed,
                        item_count=int(len(self.item_weights)),
                        min_weight=float(self.item_weights.min()),
                        max_weight=float(self.item_weights.max()),
                        manifest_sha256=manifest_sha256,
                        iterations=self.iterations,
                        last_epoch=epoch,
                    ),
                    ensure_ascii=False,
                    indent=2,
                ),
                encoding="utf-8",
            )

    return WeightedDsBatchSampler


# {
#   責務: [install_sampler: 公式の basics.base_task が使う DsBatchSampler を重み付き版へ差し替える(学習の実行前に呼ぶ)]
# }
def install_sampler(
    base_task, name_weights: dict, seed: int, manifest_sha256: str, report_path: Path
) -> None:
    base_task.DsBatchSampler = make_weighted_sampler_class(
        base_task.DsBatchSampler, name_weights, seed, manifest_sha256, report_path
    )


# {
#   責務: [read_report: 学習後に、重み付きサンプラーが実際に使われた記録を読む]
#   エラー: [記録が無い・1度も抽出されていないとき RuntimeError(使われずに成功扱いにならない)]
# }
def read_report(report_path: Path) -> dict:
    if not report_path.is_file():
        raise RuntimeError(
            "重み付きサンプラーの使用記録がありません。サンプラーが差し替わっていない可能性があります。"
        )
    report = json.loads(report_path.read_text(encoding="utf-8"))
    if not report.get("applied") or report.get("iterations", 0) < 1:
        raise RuntimeError("重み付きサンプラーが学習中に1度も使われていません。")
    return report
