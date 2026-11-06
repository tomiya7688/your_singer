from __future__ import annotations

import hashlib
import json
import random
from pathlib import Path

SAMPLER_VERSION = "weighted-bucket-v1"
MIN_WEIGHT = 0.25
MAX_WEIGHT = 2.0


# 専用Python(パッケージ外)から読み込まれるため、相対importを使わない。
def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


# {
#   責務: [load_weight_manifest: train_weights.json を検証して、音声ファイル名→重みの対応を返す]
#   引数: [manifest_path: train_weights.json, expected_sha256: dataset.json に記録されたsha256, job_id: ジョブID]
#   戻り値: [weights: audio_filename→training_weight, sha256: 実ファイルのsha256]
#   エラー: [sha256不一致(改竄)・ジョブ違い・重複・範囲外または非有限のweightで ValueError(等重みへ黙って落とさない)]
# }
def load_weight_manifest(
    manifest_path: Path, expected_sha256: str, job_id: str
) -> tuple[dict, str]:
    actual = sha256_file(manifest_path)
    if actual != expected_sha256.lower():
        raise ValueError("重みmanifestのsha256が学習入力の記録と一致しません(改竄または差し替え)。")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if manifest.get("job_id") != job_id:
        raise ValueError("重みmanifestのジョブIDが要求と一致しません。")
    weights: dict[str, float] = {}
    for item in manifest.get("items", []):
        name = item["audio_filename"]
        weight = item["training_weight"]
        if name in weights:
            raise ValueError(f"重みmanifestに同じ音声が重複しています: {name}")
        if (
            not isinstance(weight, (int, float))
            or weight != weight
            or weight in (float("inf"), float("-inf"))
            or not MIN_WEIGHT <= weight <= MAX_WEIGHT
        ):
            raise ValueError(f"重みが範囲({MIN_WEIGHT}〜{MAX_WEIGHT})外または不正です: {name}")
        weights[name] = float(weight)
    if not weights:
        raise ValueError("重みmanifestにitemがありません。")
    return weights, actual


# {
#   責務: [make_weighted_sampler_class: 公式の DistributedBucketSampler を、エポックごとに重みで再抽出する派生クラスにする]
#   処理: [
#     1: 構築時に、データセットの全itemが重みを持つことを1対1で照合する(欠落は失敗)
#     2: 各エポックで、各bucketの要素を (seed, epoch) 固定の乱数で重み比例に復元抽出して置き換える
#     3: 公式の __iter__ に渡し、シャッフル・バッチ化・DDP分割は公式のまま行う。終了後にbucketを元に戻す
#     4: 使用状況(件数・最小/最大・エポック数)をreport_pathへ書く
#   ]
#   補足: [bucketの大きさを変えないため、公式の num_samples・len・DDP の前提を保つ。全rankが同じ抽出を行う]
# }
def make_weighted_sampler_class(
    base, weights_by_name: dict, seed: int, manifest_sha256: str, report_path: Path
):
    class WeightedDistributedBucketSampler(base):
        def __init__(self, dataset, *args, **kwargs):
            super().__init__(dataset, *args, **kwargs)
            names = [Path(item[0]).name for item in dataset.audiopaths_sid_text]
            missing = [name for name in names if name not in weights_by_name]
            if missing:
                raise ValueError(
                    f"重みの無い学習itemがあります(等重みで続行しません): {missing[:3]}"
                )
            self.item_weights = [weights_by_name[name] for name in names]
            self.iterations = 0
            self._write_report(None)

        def __iter__(self):
            rng = random.Random(f"{seed}:{self.epoch}")
            original = self.buckets
            self.buckets = [
                [
                    bucket[index]
                    for index in rng.choices(
                        range(len(bucket)),
                        weights=[self.item_weights[i] for i in bucket],
                        k=len(bucket),
                    )
                ]
                for bucket in original
            ]
            try:
                iterator = super().__iter__()
            finally:
                self.buckets = original
            self.iterations += 1
            self._write_report(self.epoch)
            return iterator

        def _write_report(self, epoch):
            report_path.write_text(
                json.dumps(
                    dict(
                        sampler_version=SAMPLER_VERSION,
                        applied=True,
                        seed=seed,
                        item_count=len(self.item_weights),
                        min_weight=min(self.item_weights),
                        max_weight=max(self.item_weights),
                        manifest_sha256=manifest_sha256,
                        iterations=self.iterations,
                        last_epoch=epoch,
                    ),
                    ensure_ascii=False,
                    indent=2,
                ),
                encoding="utf-8",
            )

    return WeightedDistributedBucketSampler


# {
#   責務: [install: 公式の data_utils.DistributedBucketSampler を重み付き版に差し替える(train_ms.py の実行前に呼ぶ)]
#   エラー: [data_utils に DistributedBucketSampler が無い(固定版でない)とき AttributeError]
# }
def install(
    data_utils, weights_by_name: dict, seed: int, manifest_sha256: str, report_path: Path
) -> None:
    base = data_utils.DistributedBucketSampler
    data_utils.DistributedBucketSampler = make_weighted_sampler_class(
        base, weights_by_name, seed, manifest_sha256, report_path
    )


# {
#   責務: [read_report: 学習後に、重み付きサンプラーが実際に使われた記録を読む]
#   戻り値: [report: サンプラーの使用記録]
#   エラー: [記録が無い・1度も反復されていないとき RuntimeError(サンプラーが使われずに成功扱いにならない)]
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
