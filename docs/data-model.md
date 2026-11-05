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
  "accepted": true,
  "training_assessment": {
    "talk": {
      "eligibility": "eligible",
      "quality_confidence": 0.82,
      "coverage_utility": 0.65,
      "training_weight": 0.78,
      "reason_codes": []
    },
    "singing": {
      "eligibility": "ineligible",
      "quality_confidence": null,
      "coverage_utility": null,
      "training_weight": 0.0,
      "reason_codes": ["speech_segment"]
    }
  }
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

学習ジョブにはユーザー指定の `target` と `texture_preset_id/version`、`SpeakerDataInventory` fingerprint、選択した `strategy_id/version/provider`、複数stageの学習計画、stageごとの `training_mode`（auto / fine_tune / from_scratch）、利用checkpointとそのversion、weighting policy/version、データ分割条件、評価結果を保存します。保存契約と原本・派生データの境界は `docs/design/v1-training/learning-system-design.md` を正とします。学習戦略の追加/削除で旧ジョブを黙って違う方法に変更しません。

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

## 基本Source Profile（v1）と高度な拡張（v1.x/v2）

**v1.0.0から**話者の発話教師量・歌唱教師量・声質参照量・品質・独立フレーズ数・音素/音域カバレッジを集計し、会話主体/歌唱主体/混在/多量/少量等の重複可能な方向性を持つ基本Source Profileを学習戦略選択へ渡します。これを将来機能へ延期しません。

さらに区間別の使用適格性・品質・学習価値に基づく基本的な重み付き学習をv1必須とします。

v1.x/v2では詳細な収録条件・スタイル・Augmentation強度・動的学習配分を高度化します。Source / Segment metadataはその追加に耐える拡張可能な構造とします。

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

v1ではSource Profileの**基本的な方向性（会話/歌唱の実効量、カバレッジ、品質、声質参照）**をStrategy Registryの候補選択に利用します。以下は詳細プロフィールを使う将来の高度化候補です。

- Dataset Builderのsampling調整
- セグメントweight調整
- augmentation強度調整
- 補完 / 補正強度調整
- 実測 / 推定特徴のweight調整
- loss weighting
- Speech / Singing学習比率調整
- Training Strategy選択

Adaptive TrainingはSource Profileごとに完全に別パイプラインを作るのではなく、Universal Voice Datasetを共通基盤とし、その後段の入力方法・重み・学習戦略を変更する形を基本とします。

基本プロフィールからの学習戦略自動選択はv1必須。収録条件まで考慮した高度なSource Profileと学習レシピ動的最適化はv1.x/v2で拡張します。詳細は `design/v1-training/data-profile-strategy-selection.md` と `roadmap.md` を参照してください。

## v1: 区間ごとの学習適格性・重み情報

`training_assessment` は概念モデルであり、実装時のschemaはversionを固定して定義します。

- `eligibility`: 用途ごとの使用適格性
- `reason_codes`: 除外／補正／評価の理由
- `quality_confidence`: 測定済みなら0〜1、未計測または未校正ならnullと測定状態を別記
- `coverage_utility`: 希少音素・音域・文脈等の価値（測定状態を区別）
- `training_weight`: 対象Trainerが実際に利用した重み
- `weighting_policy_version`: 重み計算のversion
- `override`: ユーザーによる区間採否・分類の上書きと履歴

**nullを0と同一視して自動除外しません。** 品質が低くても有用な区間は原則として非ゼロweightで保持します。適格性・weightは歌唱用とトーク用で別々に設定できるようにします。

### 実装済み: TrainingAssessment の保存形式(#80 A01)

- 型は `TrainingAssessmentRecord`(`src/data/YourSinger.Data/models/`)、保存は `TrainingAssessmentRepository`。保存先は `training/assessments/<job-id>/assessment.json`、JSONはsnake_case、`schema_version` は `"1"`。
- 正本の契約は `docs/design/v1-training/contracts-and-weighting.md`「1. 入出力データ契約」。`role`(primary / feature_only / review / excluded)は任意項目、`manual_override_revision` も任意。
- 状態は `measured` / `uncalibrated` / `unavailable` / `legacy_unknown` / `invalid`。`value` が `null`(未計測)と `0`(測定済みの0)はJSONでも区別する。`measured` は値が必須、`unavailable` は値が `null`。
- `reason_codes` は閉じた語彙(user_excluded / wrong_speaker / unreadable_audio / no_usable_voice / wrong_content_type / alignment_required / rare_phoneme / speaker_unconfirmed)。speaker_unconfirmed は話者が未確定で別人かどうか曖昧な区間(needs_review)。
- 保存は同じディレクトリの一時ファイルへ書き、再読込して検証してから置換する。取消・失敗では公開済みの版を残す。`job_id` は領域外を指す文字を拒否する。不正なenum・NaN/Infinity・未知の項目・重複/非昇順の `segment_id`・eligibleで重み0などは日本語の原因付きで失敗する。
- 旧UVDの `AsrConfidence` / `AlignmentConfidence` は値だけからは推測せず、UVDに記録された状態(#63)が明示するときだけその状態を使い、それ以外は `legacy_unknown` で保持する(`TrainingAssessmentLegacyImporter`)。原UVDは変更しない。
- 未実装: 適格性・品質・価値・重みの算出(A02以降)、UI、学習器への反映。

詳細は `docs/training-strategy.md` を参照してください。
