# v1-05 実装設計: 共通特徴抽出とUniversal Voice Dataset

## 対象Issue

- #6 v1-05: 共通特徴抽出とUniversal Voice Datasetを実装する

## 目的

歌唱・トークの両方が同じ解析済みデータを参照できる共通内部データセットを構築する。

## データ構造

`metadata/universal-voice-dataset.json` を共通manifestとし、segment単位で以下を保持する。

- source_id
- speaker_id
- speech / singing / ambiguous
- transcript
- ASR confidence
- phoneme sequence / timing
- alignment confidence
- alignment method / warning
- F0 feature path
- energy feature path
- voiced / unvoiced feature path
- pitch reliability
- style / prosody
- speaker embedding
- cache key

## Feature storage

時系列特徴はmanifestへ直接埋め込まず、以下へ保存する。

```text
features/
├─ f0/
├─ energy/
└─ voicing/
```

manifestはpathのみを参照する。

## 文字起こし

Faster-Whisperをworker内でlazy loadする。

- 日本語を指定
- segment WAV単位で処理
- avg_logprobからASR confidenceを算出
- worker外へASRライブラリ依存を漏らさない

## 音素列

日本語テキストはpyopenjtalkで音素列へ変換する。

同梱MFAの`align_one`へ区間専用の発音辞書を渡し、指定音素列の境界を実測する。音素数・順序・時刻を検証できた場合だけforced alignment方式を記録する。MFA JSONには校正済み信頼度がないため、信頼度0と警告を保存する。[配布資源と制約](forced-alignment.md)を参照する。

未対応音素や音素列不一致の場合は、Faster-Whisperの単語時刻と音素化結果が一致する範囲で推定時刻を保存する。単語内境界は推定値として歌唱学習へ渡さない。単語の音素列も一致しない場合は時刻を作らず、方式を`unavailable`にする。実測できなかった理由を`alignment_warning`へ保存する。

文字起こしをユーザーが修正した場合は、以前の音素時刻とalignment confidenceを破棄し、phoneme_countsを保存済み音素列から再集計する。最新の修正歌詞をワーカーへ渡して再実測し、その歌詞のhashもキャッシュキーへ含める。

## F0 / energy / voiced-unvoiced

PCM16 WAVを40 ms窓 / 10 ms hopで解析する。
自己相関の分子はゼロ詰めFFT、遅延ごとのエネルギーは累積二乗和で計算し、正規化の定義を保ってPython二重ループを避ける。

- F0: 自己相関
- energy: RMS
- voiced / unvoiced: pitch confidence

F0値ごとにもconfidenceを保存する。

## Style / Prosody

初期実装は以下の統計量を保存する。

- speaking rate
- pitch range
- energy variation
- pause ratio

## Speaker embedding

#4で作成済みのspeaker centroidを共通datasetの各segmentへ参照コピーする。

同じembeddingを再抽出しない。

## Coverage schema

次Issueの50音・音素カバレッジ診断で使えるよう、以下を先に持つ。

- phoneme_counts
- content_type_counts

## Provenance

各featureについて以下を保存する。

- segment_id
- feature_name
- stage_version
- method
- cache_key
- generated_at

キャッシュされた特徴を再利用した場合もprovenanceを保持する。

## Cache

segment音声のSHA-256とstage versionからcache keyを生成する。

cache keyが一致し、featureファイルが存在する場合はworker処理を省略する。

## User override

ユーザーによる補正を `overrides` に保存する。

v1-05ではまず文字起こし補正を実装する。

再構築時にもoverrideを再適用する。

## Dataset view

`UniversalDatasetViewBuilder` により同じUniversal Voice Datasetから以下を派生する。

- Talk view: speech
- Singing view: singing

これにより歌唱用・トーク用builderが別々の特徴抽出を行わない。
