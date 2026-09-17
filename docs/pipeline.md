# 処理パイプライン

## 概要

`your_singer` は、入力素材をそのまま各モデル学習器へ渡さず、共通の前処理・解析パイプラインを通してから歌唱 / トーク向けの学習データへ分岐させます。

```text
Sources
  ↓
Media Scan
  ↓
Audio Extraction / Decode
  ↓
Separation / Denoise / Normalize
  ↓
Segmentation / VAD
  ↓
Speaker Embedding / Clustering
  ↓
User Speaker Selection / Merge / Reject
  ↓
Speech vs Singing Classification
  ↓
ASR / Phoneme / F0 / Energy / Timing
  ↓
Quality Analysis
  ↓
Optional Completion & Correction
  ↓
Universal Voice Dataset
  ├─→ Singing Dataset Builder → Singing Trainer → Singing Exporter
  └─→ Talk Dataset Builder    → Talk Trainer    → Talk Exporter
```

## 1. Media Scan

入力は単一ファイルまたはフォルダです。フォルダを主用途として設計します。

フォルダ入力時は再帰的に走査し、対応する動画 / 音声だけを登録します。

各ソースには最低限以下を記録します。

- source id
- path
- type (video / audio)
- size
- modified time
- content hash
- duration
- decode status
- analysis version

## 2. Audio Extraction / Decode

動画から音声ストリームを抽出し、後段で扱いやすい内部PCM形式へ変換します。

FFmpeg等を利用し、入力コンテナ / コーデック差を前段で吸収する想定です。

元データ自体は保持し、前処理結果と区別します。

## 3. Separation / Denoise / Normalize

素材の状態に応じて以下を行います。

- ボーカル / BGM分離
- 環境ノイズ低減
- 残響の軽減候補
- 音量正規化
- クリッピング / 破損検知
- 音楽漏れスコアリング

重要なのは、処理済み音声だけを唯一の真実として扱わず、元ソース・抽出音声・分離音声・cleaned音声を追跡可能にすることです。

## 4. Segmentation / VAD

長尺素材を学習に適した区間へ分割します。

各区間について以下を保持します。

- source range
- start / end
- voiced probability
- silence ratio
- overlap probability
- preliminary quality score

## 5. Speaker Analysis

各区間から話者埋め込みを抽出し、プロジェクト全体でクラスタリングします。

ファイル単位で `Speaker 1` を作るのではなく、異なるファイルに登場する同一人物をできる限り一つの話者クラスタへ統合します。

## 6. User Speaker Review

自動話者認識結果は確定扱いにしません。

ユーザーが行える操作:

- 話者ごとの代表音声試聴
- 一つ以上の話者を学習対象として選択
- 複数クラスタの統合
- 誤った区間の除外
- セグメントの採用 / 不採用変更

この修正は後段の学習データ生成へ反映し、可能な限り前段解析を再実行しません。

## 7. Speech / Singing Classification

セグメントごとに以下を推定します。

- speech
- singing
- unknown / ambiguous

分類信頼度も保存します。

歌唱モデルには singing を中心に、トークモデルには speech を中心に利用します。

## 8. Feature Extraction

共通特徴として以下を候補とします。

- text / transcription
- phonemes
- phoneme timing
- F0
- energy
- voiced / unvoiced
- speaker embedding
- style / prosody embedding
- quality metrics
- noise / music / reverb scores

歌唱側ではMIDI相当のノート情報を元素材から直接持たない場合、F0や音素タイミングから学習に必要な表現へ変換します。

## 9. Coverage / Quality Analysis

### 発音

- 50音カバレッジ
- 音素カバレッジ
- 各音素の出現量
- 文脈バリエーション
- 拗音 / 濁音 / 半濁音 / 促音 / 撥音 / 長音
- 拡張音素

### 音響

- SNR相当指標
- BGM漏れ
- 残響
- 音割れ
- 音量不足
- 話者混入
- ASR信頼度
- 音素アラインメント信頼度
- F0信頼度

### 歌唱追加評価

- 音域
- 音域別母音分布
- 長音
- ピッチ安定性
- 子音→母音遷移

品質不足は原則として警告であり、学習禁止ではありません。

## 10. Completion & Correction

デフォルトONです。

### ON時の候補処理

- 欠損音素推定
- 少量音素の補助
- 外れ値抑制 / 除外
- 誤認識補正
- 発声条件間の補間
- 音域差の補間
- 他音素からの話者特徴推定
- 全体の声色整合

### OFF時

自動推定・再構成の介入を最小化し、観測データを優先します。

なお、ONは必ずしも元話者から遠ざける処理ではありません。ノイズ・誤認識・外れ値を取り除くことで、話者の中心的特徴に近づく場合があります。

## 11. Universal Voice Dataset

前処理結果は歌唱 / トークで共有可能な共通形式へ保存します。

そこから目的別Dataset Builderが必要な表現を生成します。

この層を設けることで、別の歌唱 / TTSバックエンドを将来追加するときに、素材解析から作り直す必要を減らします。

## 12. Training / Export

ユーザーの選択に応じて以下のいずれかを実行します。

- Singing only
- Talk only
- Singing + Talk

初期Exporter候補:

- Singing: OpenUtau / DiffSinger 系
- Talk: Style-Bert-VITS2 系

将来の独自形式も同じ中間データセットから生成できるようにします。
