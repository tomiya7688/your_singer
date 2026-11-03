# 話者・会話/歌唱・文字起こしの多証拠照合

## 全体設計

```text
Segmentのraw/sep/cleaned候補
  ├─ Speaker candidate（複数窓の話者embedding + 話者切替/overlap）
  ├─ Speech/Singing candidate（音響・F0・時間パターン/曲伴奏の複数特徴）
  ├─ Transcript candidate（原音・整音・分離音ASR、必要時第2方式）
  └─ Transcript phonemes（日本語正規化と辞書・ユーザー修正）
        ↓
EvidenceConsistencyEvaluator
        ↓
Speaker / Content / Text / Phoneme の仮説と来歴を保存
        ↓
用途別Dataset Builder + 補完/補正 + 必要に応じたレビュー
```

複数モデルの同意をとれば必ず正しいわけではない（同じ学習データ・同じ音声劣化で共通誤りが生じる）。**一致・不一致は観測値、正解率は検証により校正した場合だけ**表示する。

## 1. 話者識別

現在の `speaker_analysis.py` はSpeechBrain EncoderClassifierから区間embeddingを抽出し、全体クラスタとcos類似度を使う。これをv1のbaselineとして保持する。

改善方式:

1. 一つの長いsegmentをそのまま1個のembeddingにせず、VADと話者切替候補に基づく短い**重複窓**で複数のembeddingを取得。最短区間などモデルの要件はbackend定義。
2. 短区間・音楽混入・重複話者・無声のみなど、判定が困難な区間に `embedding_unreliable` を付与し、無理に単一人物を確定しない。
3. 原音からのembeddingと軽処理候補を比較し、異なる収録条件でも安定したcluster centroidを作る。高品質の代表区間をanchorにし、推定だけで人物名は付けない。
4. 第2系列のspeaker encoderが利用可能なら、**モデルごとのsimilarityとcluster margin** を保持し、同一話者候補を照合する。選んだ二つのencoderの出力次元が異なるならそのまま平均しない。
5. 音楽付き歌唱で同じ人の話し声/歌声が遠くなる（ドメイン差）ケースを別評価。歌唱/発話の類似度だけで別人と確定しない。
6. 2人同時発話・合唱・重なりを検出した場合は、その区間を単一話者の純粋音声として学習しない。分離し検証できた候補のみ採用。
7. クラスタ統合・セグメント除外のユーザー修正は最優先。再クラスタで上書きしない。

`cosine_similarity` を0〜1へclampした値は**正解確率ではない**。現行 `speaker_confidence` のschema互換を保ちつつ `score_kind=cosine_similarity_clamped`、`calibration_status=uncalibrated` を追加する。

### 話者判断・比較の例

```json
{
  "segment_id": "seg_12",
  "speaker_candidates": [
    {"speaker_id": "spk_1", "model": "encoder-A", "similarity": 0.79, "margin_to_next": 0.08},
    {"speaker_id": "spk_1", "model": "encoder-B", "similarity": 0.66, "margin_to_next": 0.03}
  ],
  "overlap_status": "unknown",
  "decision": "needs_review",
  "decision_reason": ["small_margin", "cross_domain_singing"],
  "calibration": "uncalibrated"
}
```

数値はフォーマットの**例示値**であり実際の合否閾値ではない。閾値は校正用素材で評価する。

## 2. Speech/Singing分類

現行 `content_classification.py` はpitch比率・pitch安定性・有声持続等をルールで合成。ラップ、詠唱、語り調、揺れの少ない歌唱、伴奏付きMCでは境界が曖昧。

改善:

1. 短時間のwindowごとに `speech/singing/overlap/other` から得られる特徴を推定し、同一segment内で変化するなら**境界候補を作って再分割**。
2. 特徴は、歌唱専用分類器のスコア・F0持続/動的変化・音楽存在・語の音節持続・発声区間・ASR特徴など複数系列。音楽があるから歌唱、F0が安定するから歌唱、とは即断しない。
3. 少なくとも既存ヒューリスティック+独立系統の分類器/特徴の結果を比較できる経路にする。GPU不足時は計算可能な候補と未実行候補を区別。
4. 矛盾時は`ambiguous` と観測を保存し、用途別 `review` または利用可能な特徴だけで処理。違う用途への教師データ流用はしない。
5. **ユーザーに仕分けを強制しない。** 学習対象が十分あれば曖昧区間を保留して進む。修正した場合はその区間の下流のみ再計算。

保存契約:

```json
{
  "segment_id": "seg_12",
  "hypotheses": [
    {"model": "rule-v1", "raw_scores": {"speech": 0.42, "singing": 0.58}, "calibrated": false},
    {"model": "classifier-B", "raw_scores": {"speech": 0.70, "singing": 0.30}, "calibrated": false}
  ],
  "decision": "ambiguous",
  "reason_codes": ["hypotheses_disagree"],
  "manual_override": null
}
```

## 3. トーク/歌唱文字起こし

現在の `transcription_alignment.py` のfaster-whisperをbaselineとして維持し、次を追加。

1. まず候補音声（raw/sep/cleaned）へASRをかけた結果の**文字列、各時間範囲、logprob等の生スコア、モデル版**を保持。最初は適切な2候補で比較し、曖昧区間だけ追加認識。
2. 候補間の日本語文字正規化（全半角、句読点、数字、読み変換等）を行うが、原文を壊さず `normalized_text` を別保存。
3. 漢字の読み曖昧性、歌詞の伸長母音、促音/撥音、ラップ、BGM混入を誤認しやすいケースとして記録。歌唱は特に音声認識だけで歌詞が確定したとみなさない。
4. 異なるASR backendまたは辞書/言語制約で候補が違うときは、音素列整合・音響似合い度・他の候補との合意・文脈情報を使い `best_candidate` を選ぶ。**候補一致だけを正解率として表示しない**。
5. 音が無い箇所に歌詞を「補完」して教師ラベルに追加しない。歌詞なし/不確かな部分は `needs_review` または適切な非教師の話者特徴として保持。
6. 信頼度の扱い: 現行の `avg_logprob`→sigmoidは**確率校正ではない**。この変換値を `calibrated_probability` と呼ばず、raw/logprobとcalibration statusを持つ。音響雑音帯での校正誤差を別途測る。
7. 文字起こし修正後に古いMFA整列を再利用しない。トランスクリプトのfingerprintが変わるたびにphonemization/alignを影響範囲のみ再実行する。

### 候補記録例

```json
{
  "segment_id": "seg_12",
  "hypotheses": [
    {
      "id": "asr-raw",
      "input_audio_sha256": "...",
      "model_id": "faster-whisper",
      "model_revision": "...",
      "text": "きみの声が",
      "normalized_text": "きみの声が",
      "score": {"kind": "avg_logprob", "value": -0.42, "calibrated": false}
    }
  ],
  "selected_hypothesis_id": "asr-raw",
  "selection_reasons": ["phoneme_alignment_supported"],
  "manual_override_revision": null
}
```

## 4. 不一致解決と影響管理

- **独立証拠の種類別**に評価。ASRと音素アラインメントが同じ誤歌詞に引きずられても二重の正解根拠にならない。
- すべての候補が不一致なら `unresolved` を認める。訓練できない/ユーザー確認が必要な理由を記録。
- `ManualOverride`、`source_hash`、音声候補hash、各model revision、選択規則versionをfingerprintへ含める。
- 完了ではない状態を完了扱いしない。モデル未搭載・精度不足・予算不足は別reason。
- 共通UVDでは主候補のみが従来フィールドへ投影され、全hypothesisは別manifestを参照する。原データ上書きは禁止。

## 5. テスト

- MC+歌唱（同一ファイル内切替）、ラップ、歌唱高音、短い話し声、話者2人重複、BGM混入、強い圧縮。
- 独立モデルが一致/不一致するケース、正しくないが一致しているケースで確率を捏造しない。
- 歌詞修正→旧音素・alignment再利用禁止、同じハッシュなら再解析不要。
- 曖昧な歌唱区間をトーク正解へ誤配分しない。
- talk/singing双方のspeaker feature sharingは、適格性と用途の根拠を検証する。
- 評価はDER/JER、分類macro-F1、文字CER/WER、歌詞文字正解率などを使い、音声合成結果までつなげて確認する（別文書 `quality-gates.md`）。
