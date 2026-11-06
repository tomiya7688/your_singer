from __future__ import annotations

import csv
import hashlib
import json
import math
import re
import shutil
import tempfile
import wave
from pathlib import Path

SCHEMA = "your-singer-diffsinger-duration-v1"
# 公式 variance binarizer(v2.5.1, predict_dur=true)が要求する列: name, ph_seq, ph_dur, ph_num。
CSV_COLUMNS = ["name", "ph_seq", "ph_dur", "ph_num"]
GROUPING_METHOD = "mora-v1"
VOWELS = frozenset({"a", "i", "u", "e", "o"})
# 単独で1語(拍)になる音素。無音/息は測定ではなく入力に含まれる場合のみ許可する。
STANDALONE = frozenset({"N", "cl", ":", "SP", "AP"})
CONSONANTS = frozenset(
    {
        "k", "s", "t", "n", "h", "m", "y", "r", "w", "g", "z", "d", "b", "p",
        "ky", "gy", "sh", "j", "ch", "ny", "hy", "by", "py", "my", "ry",
        "f", "v", "ts", "ty", "dy", "kw", "gw",
    }
)  # fmt: skip
# 切り出し音声の長さと音素長合計の許容差(サンプル数)。
SAMPLE_TOLERANCE = 2


# {
#   責務: [group_moras: 音素列を拍(子音+母音、または単独音素)に分け、公式の ph_num(語ごとの音素数)を返す]
#   処理: [子音は直後の母音と1語にする。N/cl/長音/無音は1音素で1語。子音の後に母音が無い列は未対応として拒否する]
#   引数: [phones: 音素列]
#   戻り値: [ph_num: 語ごとの音素数(合計は音素数と一致)]
#   エラー: [未知の音素、母音の無い子音、列末の子音で ValueError]
#   補足: [語境界は測定値ではなく、拍で導出した値。provenance に導出方式を残す]
# }
def group_moras(phones: list[str]) -> list[int]:
    groups: list[int] = []
    pending = 0
    for phone in phones:
        if phone in CONSONANTS:
            pending += 1
            if pending > 1:
                raise ValueError(f"子音が連続しています(未対応): {phone}")
        elif phone in VOWELS:
            groups.append(pending + 1)
            pending = 0
        elif phone in STANDALONE:
            if pending:
                raise ValueError(f"子音の後に母音がありません: {phone}")
            groups.append(1)
        else:
            raise ValueError(f"未対応の音素です: {phone}")
    if pending:
        raise ValueError("列末の子音に母音がありません。")
    return groups


# { 責務: [_wav_seconds: PCMのWAVの長さ(秒)を返す。読めない・非PCM16モノラルは ValueError] }
def _wav_seconds(path: Path) -> float:
    try:
        with wave.open(str(path), "rb") as stream:
            if stream.getnchannels() != 1 or stream.getsampwidth() != 2:
                raise ValueError("音声がPCM16モノラルではありません。")
            if stream.getframerate() <= 0:
                raise ValueError("音声のサンプルレートが不正です。")
            return stream.getnframes() / stream.getframerate()
    except wave.Error as error:
        raise ValueError(f"音声を読めません: {error}") from error


# { 責務: [_sha256: ファイルのsha256] }
def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


# {
#   責務: [prepare_diffsinger_duration: 生成済みの音響学習入力から、公式 variance binarizer 用のDuration入力を作る]
#   処理: [
#     1: acoustic-input の provenance とjob識別情報を照合する
#     2: 各クリップの音素列・音素長・音声長を検証し、拍で ph_num を導出する
#     3: 検証できたクリップだけを duration-input へ配置し、保留したクリップと理由を provenance に残す
#   ]
#   引数: [payload: project_root, job_id, dataset_fingerprint]
#   戻り値: [summary: raw_data_dir・採用数・保留数]
#   エラー: [入力欠落・job不一致・既存出力・採用0件で失敗(元入力は変更しない)]
#   補足: [保留は破棄ではない。再解析・補完後に新しいjobで再生成できる。Duration trainer の起動は含まない]
# }
def prepare_diffsinger_duration(payload: dict) -> dict:
    job_id = payload["job_id"]
    if not isinstance(job_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,100}", job_id):
        raise ValueError("歌唱jobの識別子が不正です。")
    project_root = Path(payload["project_root"]).resolve(strict=True)
    dataset_root = (project_root / "training/diffsinger" / job_id).resolve(strict=True)
    if not dataset_root.is_relative_to(project_root):
        raise ValueError("歌唱jobが作業領域外です。")
    source = dataset_root / "acoustic-input"
    destination = dataset_root / "duration-input"
    if destination.exists() or destination.is_symlink():
        raise FileExistsError("Duration入力は作成済みです。新しいjobを作成してください。")
    provenance = json.loads((source / "provenance.json").read_text(encoding="utf-8"))
    if (
        provenance.get("job_id") != job_id
        or provenance.get("dataset_fingerprint") != payload.get("dataset_fingerprint")
        or provenance.get("schema") != "your-singer-diffsinger-acoustic-v1"
    ):
        raise ValueError("音響学習入力が、要求のjobまたはfingerprintと一致しません。")
    with (source / "transcriptions.csv").open(encoding="utf-8", newline="") as stream:
        listed = list(csv.DictReader(stream))
    names = [row["name"] for row in listed]
    if len(set(names)) != len(names):
        raise ValueError("transcriptions.csv のクリップ名が重複しています。")
    rows = {row["name"]: row for row in listed}
    provenance_names = [record["name"] for record in provenance["items"]]
    if len(set(provenance_names)) != len(provenance_names):
        raise ValueError("来歴のクリップ名が重複しています。")
    staging = Path(tempfile.mkdtemp(prefix=".duration-", dir=dataset_root))
    try:
        (staging / "wavs").mkdir()
        accepted, held = [], []
        with (staging / "transcriptions.csv").open("w", encoding="utf-8", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(CSV_COLUMNS)
            for record in provenance["items"]:
                name = record["name"]
                try:
                    row = rows[name]
                    phones = row["ph_seq"].split()
                    durations = [float(value) for value in row["ph_dur"].split()]
                    _validate_clip(source / "wavs" / f"{name}.wav", phones, durations)
                    ph_num = group_moras(phones)
                except (KeyError, ValueError, OSError) as error:
                    # 推測値を実測として通さず、理由を残して保留する(素材は消さない)。
                    held.append(dict(name=name, segment_id=record["segment_id"], reason=str(error)))
                    continue
                shutil.copyfile(source / "wavs" / f"{name}.wav", staging / "wavs" / f"{name}.wav")
                writer.writerow(
                    [name, row["ph_seq"], row["ph_dur"], " ".join(str(n) for n in ph_num)]
                )
                accepted.append(
                    dict(
                        name=name,
                        segment_id=record["segment_id"],
                        training_weight=record["training_weight"],
                        measurement=record["measurement"],
                        wav_sha256=_sha256(staging / "wavs" / f"{name}.wav"),
                    )
                )
        if not accepted:
            raise ValueError(
                "Duration入力に使えるクリップがありません: " + json.dumps(held, ensure_ascii=False)
            )
        phones = sorted({p for r in rows.values() for p in r["ph_seq"].split()})
        (staging / "dictionary-ja.txt").write_text(
            "".join(f"{phone}\t{phone}\n" for phone in phones), encoding="utf-8"
        )
        result = dict(
            schema=SCHEMA,
            job_id=job_id,
            dataset_fingerprint=provenance["dataset_fingerprint"],
            weight_manifest_sha256=provenance["weight_manifest_sha256"],
            source_acoustic_provenance_sha256=_sha256(source / "provenance.json"),
            official_binarizer="openvpi/DiffSinger v2.5.1 preprocessing/variance_binarizer.py (predict_dur=true)",
            word_grouping=dict(method=GROUPING_METHOD, measured=False),
            items=accepted,
            held=held,
            training_run=False,
            source_audio_unchanged=True,
        )
        (staging / "provenance.json").write_text(
            json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8"
        )
        staging.rename(destination)
    finally:
        # staging は本関数が job 配下に作った一時領域だけを対象に削除する。
        if staging.is_relative_to(dataset_root) and staging.exists():
            shutil.rmtree(staging)
    return dict(
        raw_data_dir=str(destination),
        item_count=len(accepted),
        held_count=len(held),
        training_run=False,
    )


# {
#   責務: [_validate_clip: 音素長と切り出し音声の長さを検証する]
#   エラー: [個数不一致・空・非有限/負/ゼロ長・音声長との不一致で ValueError]
# }
def _validate_clip(wav: Path, phones: list[str], durations: list[float]) -> None:
    if not phones or len(phones) != len(durations):
        raise ValueError("音素列と音素長の個数が一致しない、または空です。")
    if any(not math.isfinite(d) or d <= 0 for d in durations):
        raise ValueError("音素長が有限の正数ではありません。")
    seconds = _wav_seconds(wav)
    with wave.open(str(wav), "rb") as stream:
        tolerance = SAMPLE_TOLERANCE / stream.getframerate()
    if abs(sum(durations) - seconds) > tolerance:
        raise ValueError("音素長の合計が切り出し音声の長さと一致しません。")
