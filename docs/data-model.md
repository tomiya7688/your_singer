# 内部データ設計

## 目的

歌唱モデルとトークモデルで前処理を二重化せず、一度解析した素材から複数のモデル形式を生成できるように、共通の中間データ構造を持ちます。

## プロジェクト構成案

```text
project/
├─ project.json
├─ sources/
├─ extracted/
├─ separated/
├─ cleaned/
├─ segments/
├─ rejected/
├─ metadata/
│  ├─ sources.jsonl
│  ├─ segments.jsonl
│  ├─ speakers.json
│  ├─ styles.json
│  └─ coverage.json
├─ features/
│  ├─ phonemes/
│  ├─ f0/
│  ├─ energy/
│  ├─ speaker/
│  └─ style/
├─ cache/
└─ exports/
```

## Source

1つの入力ファイルを表します。

例:

```json
{
  "source_id": "src_0001",
  "path": "input/stream_001.mp4",
  "media_type": "video",
  "size": 123456789,
  "modified_time": "2026-09-18T00:00:00+09:00",
  "content_hash": "...",
  "duration_sec": 7200.4,
  "analysis_version": "0.1.0"
}
```

## Segment

学習・解析の最小単位です。

例:

```json
{
  "segment_id": "seg_000152",
  "source_id": "src_0001",
  "start_sec": 120.35,
  "end_sec": 126.84,
  "audio_path": "segments/seg_000152.wav",
  "speaker_id": "spk_02",
  "speaker_confidence": 0.96,
  "content_type": "speech",
  "content_type_confidence": 0.93,
  "text": "今日はいい天気ですね",
  "phonemes": ["k", "y", "o", "o"],
  "quality": {
    "overall": 0.91,
    "noise": 0.08,
    "music_leak": 0.03,
    "reverb": 0.12,
    "asr_confidence": 0.95,
    "pitch_reliability": 0.88
  },
  "accepted": true
}
```

## Speaker

検出された話者クラスタを表します。

保持候補:

- speaker_id
- display_name
- representative_segments
- total_duration
- usable_duration
- embedding centroid
- merged_from
- user_selected

同一人物が複数クラスタに分割された場合、`merged_from` に元クラスタを残して統合履歴を追跡できるようにします。

## Coverage

50音・音素のカバレッジ診断結果を保持します。

例:

```json
{
  "speaker_id": "spk_02",
  "kana_coverage": {
    "observed": 43,
    "target": 46,
    "missing": ["ぬ", "ぺ", "ぽ"]
  },
  "phoneme_coverage": {
    "weak": ["gy", "ryo", "f"],
    "missing": ["..." ]
  },
  "rating": "warning"
}
```

## 補完・補正状態

補完結果は元観測値と区別して保持します。

各特徴に対して次のような状態を持てるようにします。

- observed
- weak_observed
- estimated
- corrected
- rejected

補完・補正で生成・修正した値は、どの処理が作ったものか追跡できるよう provenance を持たせることを推奨します。

例:

```json
{
  "phoneme": "n_u",
  "state": "estimated",
  "confidence": 0.86,
  "method": "cross_phoneme_completion_v1"
}
```

## 学習設定

プロジェクトごと・話者ごとに以下を保存します。

```json
{
  "completion": {
    "enabled": true,
    "mode": "auto",
    "allow_on_observed": true
  },
  "targets": {
    "singing": true,
    "talk": true
  }
}
```

これにより同じ解析結果から、補完ON/OFFや出力形式を変えて再学習できます。

## キャッシュ

キャッシュは少なくとも以下のキーで無効化できる構造にします。

- source hash
- stage name
- stage version
- relevant settings hash

例:

```text
cache key = hash(source_hash + stage + stage_version + settings_hash)
```

これにより、例えばExporterだけ更新した場合に音声分離からやり直すことを避けます。

## 設計原則

- 元データと加工結果を区別する
- 推定値と実測値を区別する
- 自動判定とユーザー修正を区別する
- 解析結果を再利用できる
- 後から別モデル形式を追加できる
- どの処理で値が変わったか追跡できる


## 将来拡張: Source Profile

v1.0.0ではSource Profileを利用したAdaptive Trainingを必須にしません。
ただし、将来追加できるようにSource / Segment metadataは拡張可能な構造にします。

Source Profileは「元データがどのような素材か」を表す解析結果です。

候補:

- recording_quality
- content_type
- music_leak
- reverb
- compression
- noise_characteristics
- speaking_style
- singing_style
- pitch_range
- dataset_density
- segment_consistency
- reliability

概念例:

```json
{
  "source_id": "src_0001",
  "source_profile": {
    "recording_quality": 0.78,
    "music_leak": 0.31,
    "reverb": 0.52,
    "compression": 0.18,
    "content_type": ["speech", "singing"],
    "pitch_range": {
      "min_hz": 92.0,
      "max_hz": 740.0
    },
    "confidence": 0.86
  }
}
```

Source Profileは将来的に次の用途で利用します。

- Dataset Builderのsampling調整
- セグメントweight調整
- augmentation強度調整
- 補完 / 補正強度調整
- 実測 / 推定特徴のweight調整
- loss weighting
- Speech / Singing学習比率調整
- Training Strategy選択

Adaptive TrainingはSource Profileごとに完全に別パイプラインを作るのではなく、Universal Voice Datasetを共通基盤とし、その後段の入力方法・重み・学習戦略を変更する形を基本とします。

Source Profileの収集・可視化はv1.x、学習戦略への本格反映はv2.x以降を想定します。詳細は `roadmap.md` を参照してください。
