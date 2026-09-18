# v1-03 実装設計: 話者解析・選択編集

## 対象Issue

- #4 v1-03: 話者ダイアリゼーション・クラスタリング・選択編集を実装する

## 目的

前処理済みセグメントから話者特徴を抽出し、ファイル単位ではなくVoice Project全体で話者クラスタを構築する。

自動解析結果は確定扱いにせず、ユーザーの選択・統合・除外を永続化し、修正時に前処理やembedding抽出をやり直さない。

## 処理

```text
segments
  ↓
speaker embedding
  ↓
project-wide clustering
  ↓
speaker centroid / confidence
  ↓
representative segments
  ↓
speakers.json
  ↓
user selection / merge / reject
```

## 話者embedding

SpeechBrain ECAPA-TDNNを使用する。

- 入力をmono / 16 kHzへ変換
- 1秒未満の素材はpadding
- embeddingをL2正規化
- GPUが利用可能ならGPUを使用
- GPUがない場合はCPUへフォールバック

モデル実装はPython worker内部に閉じ、C#側へライブラリ依存を漏らさない。

## クラスタリング

プロジェクト内の全セグメントを同時にクラスタリングする。

初期実装はcosine距離を用いた階層型クラスタリングとし、ファイル境界をクラスタ境界として扱わない。

これにより、同一人物が複数ファイルへ登場した場合も同じクラスタへ集約できる。

## Speakerデータ

各話者について以下を保存する。

- speaker_id
- display_name
- representative_segment_ids
- total_duration_sec
- usable_duration_sec
- embedding_centroid
- merged_from
- user_selected

代表サンプルはspeaker centroidへ近いセグメントを最大3件選ぶ。

## Segment割り当て

各セグメントについて以下を保存する。

- segment_id
- speaker_id
- confidence

confidenceはsegment embeddingとspeaker centroidのcosine類似度を0〜1へ制限した値とする。

## ユーザー修正

C#側の `SpeakerOverrideService` で以下を行う。

- 学習対象話者の選択 / 選択解除
- セグメントの除外 / 復帰
- 複数話者クラスタの手動統合

修正内容は `metadata/speakers.json` に保存する。

これらの操作ではML workerを再実行しない。

## 統合履歴

手動統合時は以下を残す。

- 統合先speaker ID
- 統合元speaker ID一覧
- 統合日時

Speaker側にも `merged_from` を保持する。

## 使用可能時間

自動解析直後は全割り当て区間を使用可能とする。

ユーザーがセグメントを除外した場合は、対象話者のusable durationを再計算する。

## UIとの境界

このIssueでは話者解析・編集のバックエンドを実装する。

実際の一覧表示、代表サンプル試聴、チェックボックス、統合操作画面は #13 のデスクトップUIで実装する。

UI文言は日本語で統一する。

## 再解析方針

speaker embedding / clusteringは明示的に再解析した場合のみ実行する。

ユーザーによる選択・統合・除外は保存済み解析結果を書き換えるだけとし、前処理やembedding抽出を再実行しない。
