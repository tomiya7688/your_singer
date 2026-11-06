"""合成音声2〜4件で、実際の公式binarizationを起動器経由で実行する疑似確認(実素材・実モデルは使わない)。
使い方: python tools/smoke_diffsinger_binarize.py <bundle> <空の作業領域>
"""

import json
import math
import struct
import sys
import wave
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[1]
WORKER = str(REPOSITORY / "src/process/YourSinger.Process/processing/ml/worker")
sys.path.insert(0, WORKER)
from your_singer_ml.diffsinger_binarize import binarize_diffsinger  # noqa: E402

TRAINER_ROOT = sys.argv[1]  # 構築済みのDiffSinger bundle(runtime・source を含む)
root = Path(sys.argv[2])  # 合成素材を置く空の作業領域(Eドライブ)
job = root / "training" / "diffsinger" / "smoke-job"
acoustic = job / "acoustic-input"
(acoustic / "wavs").mkdir(parents=True, exist_ok=True)
rate = 44100
rows = ["name,ph_seq,ph_dur"]
for index, freq in enumerate((220.0, 262.0, 294.0, 330.0)):
    name = f"segment_{index}"
    seconds = 1.0
    with wave.open(str(acoustic / "wavs" / f"{name}.wav"), "wb") as stream:
        stream.setnchannels(1)
        stream.setsampwidth(2)
        stream.setframerate(rate)
        frames = b"".join(
            struct.pack("<h", int(8000 * math.sin(2 * math.pi * freq * i / rate)))
            for i in range(int(rate * seconds))
        )
        stream.writeframes(frames)
    rows.append(f"{name},a i,0.5 0.5")
(acoustic / "transcriptions.csv").write_text("\n".join(rows) + "\n", encoding="utf-8")
(acoustic / "dictionary-ja.txt").write_text("a\ta\ni\ti\n", encoding="utf-8")
result = binarize_diffsinger(
    dict(
        trainer_root=TRAINER_ROOT,
        project_root=str(root),
        job_id="smoke-job",
        speaker_id="smoke",
        stage_timeout_seconds=600,
    )
)
print(json.dumps(result, ensure_ascii=False))
