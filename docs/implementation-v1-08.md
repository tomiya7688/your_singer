# v1-08 実装設計: 自動補完・補正エンジンとON/OFF制御

## 対象Issue

- #9 v1-08: 自動補完・補正エンジンとON/OFF制御を実装する

## 目的

Universal Voice Datasetを入力として、不足音素・少量音素・低信頼観測・外れ値を補完または補正し、学習用datasetの組み立てをON/OFFで切り替えられるようにする。

## 設定

`AutoCorrectionSettings.Enabled` を持ち、既定値は `true` とする。

OFF時は観測データを優先し、自動補完・自動破棄を適用しない。

## 補正状態

各補正対象は以下の状態を持つ。

- observed
- weak_observed
- estimated
- corrected
- rejected

各記録に以下を保存する。

- segment_id
- phoneme
- state
- method
- confidence
- original_value
- corrected_value
- reason

## ON時の処理

初期実装では以下を行う。

- 欠損音素を `missing-phoneme-estimation` として推定対象へ登録
- 少量音素を `weak_observed` とし、他音素からの話者特徴補助推定対象にする
- 低confidence音素を文脈整合補正対象へ登録
- ASR / alignment confidenceが極端に低いsegmentをrejectedにする
- 歌唱pitch reliabilityが低いsegmentを音域・発声条件間の補間対象にする

補正処理は観測値を上書きして出自を消すのではなく、補正記録を別レイヤーとして保持する。

## OFF時の処理

全segmentを `observed` として扱い、自動補完・自動破棄を適用しない。

これによりON/OFFで同じ解析済みデータを再利用できる。

## Dataset差分

`CorrectedDatasetView.Fingerprint` に以下を含めたSHA-256を保存する。

- Universal Voice Dataset stage version
- ON/OFF
- correction state
- method
- confidence

これにより設定違いの学習datasetを追跡可能にする。

## 永続化

```text
metadata/auto-correction.json
```

へ以下を保存する。

- enabled
- stage_version
- correction records

## UI方針

ON/OFFは「本人らしさ」の優劣として説明しない。

- ON: 不足・不自然な観測値を自動補完・補正する
- OFF: 観測済みデータを優先して利用する

という処理方針の違いとして表示する。
