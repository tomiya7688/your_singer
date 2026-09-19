# v1-04 実装設計: Speech / Singing分類と用途別振り分け

## 対象Issue

- #5 v1-04: Speech / Singing分類と用途別データ振り分けを実装する

## 目的

同一素材内に混在する会話と歌唱をsegment単位で分類し、トークモデル用データと歌唱モデル用データへ分けて利用できるようにする。

## 分類対象

v1-02で生成済みの `segments/` 内WAVを入力とする。

前処理済みsegmentだけを再利用するため、再分類時に以下はやり直さない。

- 動画からの音声抽出
- BGM分離
- ノイズ低減
- 音量正規化
- VAD / segment生成

## 分類ラベル

内部ラベルは以下の3種類とする。

- `speech`
- `singing`
- `ambiguous`

confidenceに加えてspeech scoreとsinging scoreも保存する。

## 初期分類器

v1-04では前処理済みPCM WAVから次の音響特徴を算出して分類する。

- voiced frame比率
- pitch検出率
- pitch安定性
- 持続発声率
- zero crossing rate
- energy dynamic range

歌唱では連続した有声音と安定したpitchが多く、会話ではpitch変化とenergy変化が大きくなりやすい特徴を利用する。

判定差が小さい場合や両スコアが低い場合は `ambiguous` とする。

この分類器はworker内部のadapterとして扱い、後から学習済み分類モデルへ差し替えてもC#側の契約は変更しない。

## 永続化

分類結果は以下へ保存する。

```text
metadata/content-classification.json
```

segmentごとに以下を保存する。

- segment_id
- content_type
- confidence
- speech_score
- singing_score
- user_overridden

## 用途別振り分け

`UsageDatasetRouter` で分類結果を以下へ振り分ける。

- speech → TalkSegmentIds
- singing → SingingSegmentIds
- ambiguous → AmbiguousSegmentIds

ambiguousは自動では歌唱・トークのどちらにも投入しない。

ユーザーが分類を修正した場合は、その修正後ラベルに従ってdatasetへ入る。

## ユーザー修正

`ContentClassificationOverrideService` によりsegment単位で分類を変更できる。

修正時には以下を履歴として保存する。

- segment_id
- original_type
- new_type
- changed_at

修正後は `user_overridden = true` とする。

## 再分類

自動分類を再実行した場合でも、保存済みユーザー修正を再適用する。

これにより分類器の更新や再解析でユーザー判断が失われない。

## ライブ素材の扱い

1本のライブ動画から生成された複数segmentについて個別に分類する。

そのため、

```text
MC → speech → トーク候補
歌唱 → singing → 歌唱候補
判定困難 → ambiguous
```

として同一source内でも用途別に分離利用できる。

## UIとの境界

このIssueでは分類・永続化・用途別振り分けのバックエンドまで実装する。

UI上の分類表示・手動変更操作は #13 で実装する。

UI文言は日本語で統一する。
