# v1-11 実装設計: Style-Bert-VITS2トークモデル生成・Exporter

## 対象

- Issue #12
- Style-Bert-VITS2 2.7.0 をv1互換基準として固定する
- Universal Voice DatasetのSpeech用途を入力とする

## 推論成果物

v1では対象推論環境へ渡す成果物を次の3点に固定する。

```text
<model-name>/
├─ config.json
├─ style_vectors.npy
└─ <model-name>_e<epoch>_s<step>.safetensors
```

`style_vectors.npy` はStyle-Bert-VITS2公式前処理で生成されたもののみを採用し、
your_singer側で疑似vectorを生成しない。

## Dataset Builder

v1-09のTalk学習jobを入力にし、対象話者のSpeech segmentだけを利用する。

Voice Project内に次を生成する。

```text
training/style-bert-vits2/<job-id>/
├─ raw/
├─ esd.list
└─ dataset.json
```

`esd.list` は公式前処理へ渡す4列形式とする。

```text
<audio path>|<speaker>|JP|<text>
```

公式text preprocessing後はphones / tones / word2phを含むcleaned datasetへ変換される。

`dataset.json` にはyour_singer側の追跡情報として以下を保持する。

- segment ID
- transcript
- phoneme列
- speaker
- style/prosody特徴
- dataset fingerprint
- 自動補完・補正ON/OFF

## Trainer連携

Style-Bert-VITS2本体はProcessへ直接組み込まず、ML workerから同梱trainerを起動する。

```text
workers/
└─ style-bert-vits2/
   ├─ preprocess_all.py
   └─ train_ms.py
```

v1では以下を順番に実行する。

```text
python preprocess_all.py -m <model-name>
python train_ms.py -m <model-name>
```

いずれかが存在しない場合や終了コードが0以外の場合は失敗扱いとする。
標準出力・標準エラーはVoice Project内の `trainer.log` に保存する。

## Exporter

Exporterは実在する学習成果物を要求する。

必須:

- config.json
- style_vectors.npy
- .safetensors model

v1ではモデル重みを `.safetensors` に固定する。

## Validator

`StyleBertVits2ExportValidator` は以下を検証する。

- config.jsonが存在しJSONとして読める
- style_vectors.npyが存在する
- .safetensorsが1件以上ある
- model_name
- version
- data.n_speakers
- data.spk2id
- data.num_styles
- data.style2id

## 補完・補正

学習jobの `AutoCorrectionEnabled` と `DatasetFingerprint` をdataset recordへ保存する。

OFF時はv1-08のobserved-only viewから作ったTalk jobを利用し、ON時との差を追跡可能にする。

## 実機推論試験

CIではStyle-Bert-VITS2推論環境での音声生成までは行わない。

Issue #12を完了扱いにする前に、Style-Bert-VITS2 2.7.0互換環境で以下を確認する。

1. Exporter成果物をmodel_assetsへ配置できる
2. config.json / model / style_vectors.npyが読み込まれる
3. 対象speaker/styleを選択できる
4. 任意の日本語文章を入力できる
5. 音声生成が成功する

実推論確認が終わるまでIssue #12はcloseしない。
