# v1-07 実装設計: 歌唱向け追加品質診断

## 対象Issue

- #8 v1-07: 歌唱向け追加品質診断を実装する

## 目的

50音・音素カバレッジとは別に、歌唱モデル学習で重要になる音域、ロングトーン、母音、子音→母音遷移、F0信頼度を診断する。

## 入力

Universal Voice Dataset のうち `content_type = singing` のsegmentだけを利用する。

既存の以下の特徴を再利用する。

- mean_f0_hz
- pitch_reliability
- phoneme timing
- speaker_id

重い音声解析は再実行しない。

## 診断scope

- プロジェクト全体の歌唱segment
- 話者ごとの歌唱segment

## 音域

segmentの平均F0を以下の3帯域へ分ける。

- low: 165 Hz未満
- mid: 165 Hz以上330 Hz未満
- high: 330 Hz以上

各帯域のデータ時間を集計する。

## 音域別母音

各帯域で `a / i / u / e / o` の出現数を集計する。

不足する母音があれば歌唱品質警告へ追加する。

## ロングトーン

母音音素の継続時間が300 ms以上のものを持続母音として集計する。

- 件数
- 合計時間
- pitch reliabilityを用いたlong tone quality

を保存する。

## 子音→母音遷移

連続する音素について、子音の次が母音の場合に `consonant->vowel` の頻度を保存する。

## Pitch reliability

歌唱segmentのpitch reliability平均値を保存する。

低い場合は注意を出すが、学習を禁止しない。

## 品質判定

- Good
- Warning
- Insufficient

の3段階とする。

警告はすべて非ブロッキングであり、学習継続可否とは分離する。

## 保存

詳細:

```text
metadata/singing-quality.json
```

共通Coverage schemaにはsummaryとして以下を保存する。

- scope_count
- overall_quality

これにより後続の学習前UIから発音カバレッジと歌唱品質を別軸で参照できる。
