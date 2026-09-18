# v1-04 実装設計: Speech / Singing分類と用途別振り分け

## 対象Issue

- #5 v1-04: Speech / Singing分類と用途別データ振り分けを実装する

## 目的

同じ素材の中に会話と歌唱が混在していても、セグメント単位で用途を分離し、
トークモデル用datasetと歌唱モデル用datasetへ適切に振り分けられるようにする。

## 分類種別

各セグメントを以下のいずれかへ分類する。

- speech
- singing
- ambiguous

分類結果には必ずconfidenceを保存する。

## 初期分類器

v1-04では前処理済みPCM16 WAVを入力として、以下の特徴を利用する。

- 有声音率
- F0推定の周期性
- フレーム間の音高変化
- 一定音高の持続率

一般に歌唱は周期性と音高持続が高くなりやすく、
会話は音高変化と短い有声区間の交互出現が増えやすい性質を使う。

分類器はML worker内部へ閉じる。
将来、専用のspeech / singing classifierへ差し替えても
C#側の契約を変更しない。

## ambiguous

speech / singingのスコア差が小さい区間や、
どちらのconfidenceも十分でない区間はambiguousとする。

ambiguousは初期状態では以下とする。

- トークdataset候補: ON
- 歌唱dataset候補: ON

後段の品質診断やユーザー判断で片方または両方から除外できる。

## 用途別振り分け

`ContentDatasetRoutingService` が以下を提供する。

- speech → トークdataset
- singing → 歌唱dataset
- ambiguous → 初期状態では両方

dataset builderは分類器を直接呼ばず、
保存済みのclassification metadataを参照する。

## ユーザー修正

各セグメントに `user_override_type` を保存できる。

ユーザーが分類を修正した場合は、予測値自体を消さず、
`predicted_type` と `user_override_type` を分離して保持する。

これにより自動分類結果とユーザー判断を追跡できる。

## 再分類

ユーザーによる分類修正では前処理を再実行しない。

明示的な再分類時だけML workerを呼び出す。

## 永続化

分類結果は以下へ保存する。

```text
metadata/content-classification.json
```

保存項目:

- segment_id
- predicted_type
- user_override_type
- speech_confidence
- singing_confidence
- confidence
- include_in_talk_dataset
- include_in_singing_dataset
- stage_version

## UIとの境界

このIssueでは分類・永続化・dataset振り分けのバックエンドを実装する。

実際の分類表示やユーザーによる修正UIは #13 で実装する。
UI文言は日本語で統一する。
