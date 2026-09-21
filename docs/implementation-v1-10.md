# v1-10 実装設計: OpenUtau / DiffSinger歌唱モデル生成・Exporter

## 対象

- Issue #11
- OpenUtau 安定版 0.1.565 を互換基準とする
- OpenUtau内蔵DiffSinger renderer / singer schemaを対象とする

## 互換基準

OpenUtauのDiffSinger実装では、voicebank直下の `dsconfig.yaml` から少なくとも以下を参照する。

- `phonemes`
- `acoustic`
- `vocoder`

また、duration frontendは `dsdur/dsconfig.yaml` とphoneme定義・duration modelを参照する。

your_singerでは以下を標準出力とする。

```text
SingerName/
├─ character.yaml
├─ dsconfig.yaml
├─ phonemes.txt
├─ acoustic.onnx
├─ dsdur/
│  ├─ dsconfig.yaml
│  ├─ phonemes.txt
│  └─ dur.onnx
├─ dspitch/          # 任意
│  └─ pitch.onnx
├─ dsvariance/       # 任意
│  └─ variance.onnx
└─ dsvocoder/        # 任意。ローカルvocoderを同梱する場合
```

## Dataset Builder

Universal Voice Datasetとv1-09のSinging学習jobを入力にする。

学習入力には以下を保存する。

- audio path
- transcript
- phoneme列
- phoneme duration
- F0 feature path
- energy feature path
- mean F0
- pitch reliability
- dataset fingerprint
- 自動補完・補正ON/OFF

前処理・特徴抽出は再実行しない。

## Trainer連携

DiffSinger trainer自体はmain processへ埋め込まず、ML workerからbundled trainerを起動する。

```text
workers/
└─ diffsinger/
   └─ tasks/run.py
```

が存在する場合のみ学習を開始する。trainerが同梱されていない状態を成功扱いにはしない。

標準出力と標準エラーはVoice Project内の `trainer.log` に保存する。

## Exporter

Exporterは実在する学習成果物を要求する。

必須:
- acoustic model
- duration model
- phoneme一覧

任意:
- pitch model
- variance model
- local vocoder

必須成果物が欠ける場合はexportを失敗させる。

## 検証

`DiffSingerExportValidator` で以下を機械検証する。

- character.yaml
- dsconfig.yaml
- phonemes.txt
- acoustic.onnx
- dsdur/dsconfig.yaml
- dsdur/phonemes.txt
- dsdur/dur.onnx
- root dsconfigのphonemes/acoustic/vocoder key

## OpenUtau実機試験

CIではOpenUtau GUI上の実レンダリングまでは検証しない。

Issue #11を完了扱いにする前に、OpenUtau 0.1.565実機で以下を確認する。

1. voicebankを導入できる
2. singerとして認識される
3. 歌詞とノートを入力できる
4. DiffSinger rendererで音声生成できる
5. 欠損model/configエラーが出ない

この実機試験が未完了の間はIssue #11をcloseしない。
