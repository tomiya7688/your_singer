# 使用適格性・価値・学習重み — v1詳細設計

## 対象

- `UniversalVoiceDatasetRecord` を原本とし、観測値は改変せず、学習用評価を別recordとして追加する。
- `TrainingDatasetSnapshotService` と `TrainingJobPlanningService` の間で、ユーザー修正後の不変snapshotを評価する。
- 歌唱・トークの区間評価は別々に計算する。品質の一律除外をしない。

## 1. 入出力データ契約（提案するv1 schema）

保存位置の例: `training/assessments/<job-id>/assessment.json`（最終実装では `ProjectWorkspace` の安全なパスAPIを使用）。

```json
{
  "schema_version": "1",
  "policy_version": "training-weight-v1",
  "job_id": "job-example",
  "speaker_id": "spk_02",
  "target": "talk",
  "source_dataset_fingerprint": "sha256-...",
  "items": [
    {
      "segment_id": "seg_1",
      "eligibility": "eligible",
      "reason_codes": [],
      "quality": {
        "value": 0.72,
        "status": "measured",
        "components": {
          "audio": {"value": 0.6, "status": "measured"},
          "asr": {"value": 0.84, "status": "measured"},
          "alignment": {"value": null, "status": "uncalibrated"}
        }
      },
      "utility": {"value": 0.75, "status": "measured", "reason_codes": ["rare_phoneme"]},
      "raw_weight": 1.0,
      "training_weight": 1.0
    }
  ]
}
```

- status: `measured`（0〜1の校正済み値）、`uncalibrated`（結果はあるが確率解釈不可）、`unavailable`（算出不可）、`legacy_unknown`（旧ファイルから判別不可）、`invalid`（無効値）。
- eligibility: `eligible`, `needs_review`, `excluded`。評価の対象用途（talk/singing）ごとに生成する。除外の理由は機械可読なreason_codesを持つ。
- `quality.value` が null の場合と0の場合は異なる。UIは「未計測」を0%と表示しない。
- `training_weight` は有限数（NaN/±∞禁止）かつeligibleなら正、excludedなら0。needs_reviewは学習器へ渡す前に処理対象から分離し、確認待ちとして残す。
- recordには使った入力dataset fingerprint、manual override revision、policy version、必須のtrainer targetを保持する。配列はSegmentId昇順。
- 保存は一時ファイル→検証→原子的置換／公開。相対パス・hash・IDを照合し、領域外の参照を拒否。

### 既存形式の移行

現行 `UniversalVoiceSegment.AsrConfidence` や `AlignmentConfidence` は `double` で、0が「測定済みゼロ」か「未計測」か区別できない。**値だけから推測して埋めない**。測定元／方法が明示的に特定できる場合のみ `measured` とし、それ以外は `legacy_unknown` にする。記録再計測が必要なら後段の再解析導線へ送る。

旧学習ジョブや旧manifestを黙って新しい重み形式へ読み替えない。従来のジョブ履歴は表示できるが、新規policyでの再実行は「ジョブ再作成」を必要とする（`TrainingJobPlanningService.RequiresRecreation` と整合）。

## 2. 使用適格性（qualityとは独立）

判定順を固定する。最初に該当した理由をprimary reasonとして記録し、副次理由は追加可能。

1. ユーザーが明示除外 → `excluded` / `user_excluded`
2. 対象話者ではないことが確定している → `excluded` / `wrong_speaker`
3. 音声ファイル不在・破損・有効区間が無い → `excluded` / `unreadable_audio` や `no_usable_voice`
4. 用途と内容が明らかに不一致 → `needs_review` / `wrong_content_type`（補助特徴抽出とは独立）
5. 歌唱で検証済み音素境界が未用意 → `needs_review` / `alignment_required`（音声そのものを捨てない）
6. 対象話者・分類・必要な入力が揃う → `eligible`

話者の自動スコアが低いだけで `wrong_speaker` としない。疑わしいものは `needs_review`。誤話者・破損等の確証がない場合、**「低品質」という一語を除外理由にしない**。他用途で使える可能性を消さない。

`needs_review` がある場合も他のeligible区間を使って学習できる。eligibleが0件なら「利用可能区間なし」と理由別内訳を表示し、データを捏造しない。

## 3. v1の決定的な重み計算（初期policy）

この式は **v1の初期仕様と回帰テストの基準**。モデル実測で改善が確認できた変更はpolicy versionを更新する（過去結果を書き換えない）。

- 対象話者×用途ごとにeligible集合 `E` を確定する。
- 各区間 `i` の `Q_i`（品質）と `U_i`（学習価値）は0〜1。未計測は**評価上の中立値0.5**を使用するが、保存状態はunknownのまま。
- `quality_factor = 0.35 + 0.65 * Q_i`
- `utility_factor = 1.0 + 0.75 * U_i`
- `raw_i = quality_factor * utility_factor`
- `mean_raw = Σ(raw_i) / |E|`（eligible件数が0ならエラー）
- `training_weight_i = clamp(raw_i / mean_raw, 0.25, 2.0)`
- `excluded` と `needs_review` の `training_weight` は0（`needs_review` は未採用であることを別表示）。
- 同一話者×用途×snapshot×policyなら入力順・ファイル列挙順に関係なく同じ結果とする。

この式は **同じ希少性なら高品質の重みを高く**し、低品質でも適格なら正の重みを残す。例: `Q=1, U=0` のrawは1.0、`Q=0, U=1` のrawは0.6125（非ゼロ）。負値、非有限値、境界超過は拒否／記録する。

### Q算出

1. 利用できる校正済み指標から、用途に関係するものだけ抽出する。初期候補: audio品質（noise, music_leak, reverb, clippingから適合した0〜1品質値）、ASR confidence、phoneme alignment confidence。歌唱ではF0 reliabilityを追加。
2. `measured` の項目だけの算術平均をQとする（すべて同等重みのv1初期実装）。
3. `uncalibrated`、`legacy_unknown`、`unavailable`、`invalid` は**0に変換しない**。全項目が未計測なら `Q=0.5` という評価用中立値を使い、`quality.status=unavailable` と理由を残す。
4. audio品質は現行 `AudioPreprocessingRecord.Quality` の元ファイル指標を、セグメント指標であるかのように再ラベルしない。元の粒度をprovenanceに含める。
5. audio品質の各指標の向き（高い=良い／悪い）と適用対象は `AudioQualityAnalyzer` とテストに合わせて確定する。測定されない品質項目は無視。
6. true value 0.0（測定済み）は低品質として扱う。**MFAの未校正0は `uncalibrated` として値を使わない**（既存 #63）。

### U算出（観測根拠がある音素・音域のみ）

- 発話: 同一話者の対象用途に利用可能な全区間から、確実に観測できた音素の出現数 `n(p)` を数える。音素希少度 `r(p)=1/sqrt(max(1,n(p)))`、区間の `U` は区間内観測音素の最大 `r(p)`。信頼できる観測がなければ0.5（価値未計測）。
- 歌唱: 同じ音素希少度と、利用可能な有声音高から作る**6半音幅**の音域bin希少度を平均する。片方しか測れない場合は片方、両方なしは0.5。
- 音素はユーザー確定ラベルまたは有効な検証済み解析から採用する。欠損した音素を無根拠に「希少観測」と数えない。
- F0が0、負値、非有限、信頼性不足なら音域binを計算しない。音域bin境界は `floor(12*log2(f0Hz/55)/6)`（Hz基準55、A1）、頻度0のbinは生成しない。
- 他話者の全体頻度を混ぜず、話者×用途ごとに計算する。

`U` の完全高度化はv2のSource Profileに属する。ここでは「品質だけで貴重な音を捨てない」ための最低限を決定的に実装する。

## 4. 学習器への反映・二重適用防止

重みはDataset Builderが `segment_id` と紐付けて出力し、**Trainerが実際に消費した証跡**を残す。

v1初期実装は「weighted sampling」を第一候補とする。Trainerの既存のsamplingと衝突する場合は、対象固定バージョンで実行可能なper-sample loss weightingを検証して選ぶ。**samplingとlossを両方同時適用してはいけない**。

samplingはrandom seed、epoch、ジョブfingerprintに対して決定的、学習用集合だけで実施する。検証用集合にはweightを掛けず、評価結果を歪めない。weightを利用できないTrainerに `weighting_enabled=true` のジョブを通して「重み付き」と表示しない。

サンプリング重複が過大な少量区間については、同一ファイル・同一区間の重複管理・最大weightをテストし、過学習リスクを表示する。実装上のサンプラー／損失の方法は別のBackend Issueで固定し、差分テスト（有効な2区間のweightを入れ替えると学習抽出割合またはlossが変化する）を必須とする。

## 5. 再計算・指紋・キャッシュ

ジョブ入力fingerprintには必ず次を含める。

- 観測データsnapshot hash（元音声／手動話者修正／分類修正／補正ON/OFF／補完候補）
- 参加区間と用途別eligibility、reason、quality measurement status
- 区間ごとのweightsとpolicy version
- 学習方式、バックエンド識別／固定revision、使うcheckpointのhash（新規学習の場合は未使用と明記）
- 分割seed / trainer設定（出力差に影響するもの）

既存の `CreateJobFingerprint` はバージョンを上げるか、学習契約専用fingerprintを追加する。旧履歴は閲覧可能、再学習は必ず新ジョブ。Jobごとのassessment保存時もhash検証し、終了済みjobの入力差替えは拒否する。原素材・Universal Voice Dataset・上書き履歴を改変しない。

## 6. 必須自動テスト

- 同じspeaker/target/snapshotの並び替えに対して同じweightとfingerprint
- `Q=1,U=0` vs `Q=0,U=1`、低品質かつ有用な区間もpositive weight
- 除外・確認待ちはweight 0、eligible件数0は学習計画不可
- missing/uncalibrated confidenceと測定済み0.0を区別（#63）
- 異なる話者・対象用途ごとのweightが混線しない
- 音素観測不足と偽の希少音素によるweight過大化を防止
- 同じ入力なら同じfingerprint、修正・policy変更なら別fingerprint
- 実Trainer消費のサンプル割合もしくはlossがweightにより変化する（mock値渡しだけでは不合格）

関連既存: `tests/YourSinger.Process.Tests/TrainingSafetyTests.cs`、`TrainingRecreationTests.cs`、`InputAndCoverageTests.cs`、`tests/worker/`。
