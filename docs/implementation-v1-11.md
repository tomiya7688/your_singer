# v1-11 実装設計: Style-Bert-VITS2トークモデル生成・Exporter

## 対象

- Issue #12
- Style-Bert-VITS2 2.7.0 をv1互換基準として固定する
- 公式ソースの基準は [`d8148f3`](https://github.com/litagin02/Style-Bert-VITS2/tree/d8148f3090ee5038ca7b4e4b327116c64467f952) とする
- Universal Voice DatasetのSpeech用途を入力とする

## 推論成果物

v1では対象推論環境へ渡す成果物を次の3点に固定します。

```text
<model-name>/
├─ config.json
├─ style_vectors.npy
└─ <model-name>_e<epoch>_s<step>.safetensors
```

`style_vectors.npy` はStyle-Bert-VITS2公式前処理で生成されたもののみを採用し、
your_singer側で疑似vectorを生成しません。

## Dataset Builder

v1-09のTalk学習jobを入力にし、対象話者のSpeech segmentだけを利用します。

Voice Project内に次を生成します。

```text
training/style-bert-vits2/<job-id>/
├─ raw/
├─ esd.list
└─ dataset.json
```

`esd.list` は公式前処理へ渡す4列形式とします。

```text
<audio path>|<speaker>|JP|<text>
```

音声パスは `raw/` からの相対名とします。例えば `raw/segment.wav` は
`segment.wav|話者名|JP|書き起こし` と保存します。
公式の `--correct_path` はこの名前に `wavs/` を付けるため、`raw/` をリストへ含めない。
アプリは区切り文字 `|`・改行・空文字を含む話者名や書き起こしを拒否し、内容を黙って置換しません。

公式text preprocessing後はphones / tones / word2phを含むcleaned datasetへ変換される。

`dataset.json` にはyour_singer側の追跡情報として以下を保持します。

- segment ID
- transcript
- phoneme列
- speaker
- style/prosody特徴
- dataset fingerprint
- 自動補完・補正ON/OFF

## Trainer連携

Style-Bert-VITS2本体はProcessへ直接組み込まず、ML workerから同梱trainerを起動します。

```text
workers/
└─ style-bert-vits2/
   ├─ runtime/python.exe
   ├─ resample.py
   ├─ preprocess_text.py
   ├─ bert_gen.py
   ├─ style_gen.py
   ├─ train_ms.py
   ├─ configs/config.json
   ├─ default_config.yml
   ├─ style_bert_vits2/
   ├─ dict_data/
   ├─ pretrained/
   ├─ bert/
   ├─ slm/
   └─ model-cache/
```

### 背景と目的

MLワーカーの実行ファイルはPythonスクリプトの実行環境として使えません。
Trainerは専用の `runtime/python.exe` から起動します。
公式の `train_ms.py -m` はモデル名ではなく学習データフォルダを要求します。
アプリはDataset Builderの出力先と、ジョブID・データ指紋・補正ON/OFFを渡して照合します。

### 処理方法

アプリは入力原本を保管し、同じジョブの `trainer/data/raw/` と `trainer/data/esd.list` へ複製します。
公式既定のJSON・YAMLからジョブ専用設定を作り、次を順番に実行します。

1. `resample.py`: 44,100 Hzへ変換します。
2. `preprocess_text.py --correct_path --yomi_error raise`: 音素・アクセント・文字音素対応を作ります。
3. `bert_gen.py`: BERT特徴を作ります。
4. `style_gen.py`: 公式スタイル特徴を作ります。
5. `train_ms.py --config <trainer/data/config.json> --model <trainer/data> --assets_root <trainer/model_assets>`: 学習します。

`preprocess_all.py` は工程が失敗した場合もCLI自体がゼロ終了する場合があるため、各工程を直接起動して終了コードを確認します。
アプリは学習前に全入力の音声・BERT・スタイル特徴を確認し、欠落・重複・空のリストを拒否します。
設定・辞書・一時ファイル・特徴はジョブ内へ保存し、同梱ソースの設定を書き換えません。
日本語辞書ワーカーと学習用通信ポートもジョブごとに分離します。

現在の接続は非JP-Extra版に固定し、公式前処理CLIの既定値と同じbatch size 2・100 epochsを使います。音声変換時は音量正規化・無音削除を行いません。
CUDA対応GPUと対応する専用PyTorchを必須とし、CPU学習への切替は行いません。
公式2.7.0の依存条件は `torch<2.4`・`numpy<2` のため、通常のMLワーカーとはPython環境を分離します。
日本語辞書APIは `pyopenjtalk-dict==0.3.4.dev2` へ固定します。
通常版 `pyopenjtalk` とは同名のモジュールを配置するため同時導入せず、通常ワーカーとTrainerの各専用環境で固定版へ一本化します。
学習用の環境変数・一時領域・キャッシュはVoice Project内へ設定します。
取得済みの共有モデルは `model-cache/huggingface/hub` と `model-cache/torch/pyannote` から読み込み、学習中の自動取得は無効にします。

標準出力・標準エラーは `trainer/trainer.log` に保存します。
成功時は `trainer/result.json` にログ・設定・公式スタイルvector・学習重みのパスを記録します。
プロセスがゼロ終了しても推論成果物が揃わない場合は失敗とします。
既存の `trainer/` を上書きしません。再試行する場合は新しい学習ジョブを作成してください。

### 利用方法と配布の確認範囲

アプリの処理APIでは、Talk学習ジョブの作成後に `StyleBertVits2DatasetBuilder.BuildAsync` を実行し、
そのジョブを `StyleBertVits2TrainerService.TrainAsync` へ渡します。
学習後は `result.json` の参照先をExporterへ渡します。
現在はこの処理APIの接続を実装しており、画面からの一連操作は未確認です。

配布物の作成者は、公式ソース・CUDA用専用runtime・事前学習重み・BERT・WavLM・話者モデルを
一式にしてから配布確認を行います。専用runtime・固定資源の構築と配布スクリプトへの組込みを実装しました。
構築手順と確認範囲は[トークTrainerの同梱](./talk-trainer-bundle.md)を参照してください。公開配布前のライセンス確認は残っています。
通常利用者に不足依存の個別インストールを要求する形では配布を完了扱いにしません。

### ローカル確認結果（2026-10-17）

固定コミットの公式 `resample.py` を、ローカルに保管した非公開音声2区間で実行し、
両方が44,100 Hz・monoの非空WAVとして出力されることを確認しました。
公式日本語辞書ワーカーの起動・IPCによるテキスト処理・終了も確認しました。
辞書側の確認には `pyopenjtalk-dict==0.3.4.dev2` と `setuptools==80.9.0` を分離フォルダへ配置しました。

その後、同梱したCUDA版PyTorchで実音声2区間のリサンプル・公式音素/アクセント生成・BERT特徴・スタイル特徴まで実行しました。
素材は歌唱区間のため、前処理の接続確認として記録し、Speech学習データの合格とは扱いません。
GPUでの実学習・学習済みモデルの推論・画面操作・配布物全体の動作は未確認です。
実音声・ログ・依存・検証結果はEドライブ内のGit無視対象だけへ保存しています。

## Exporter

Exporterは実在する学習成果物を要求します。

必須:

- config.json
- style_vectors.npy
- .safetensors model

v1ではモデル重みを `.safetensors` に固定します。

## Validator

`StyleBertVits2ExportValidator` は以下を検証します。

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

学習jobの `AutoCorrectionEnabled` と `DatasetFingerprint` をdataset recordへ保存します。

OFF時はv1-08のobserved-only viewから作ったTalk jobを利用し、ON時との差を追跡可能にします。

## 実機推論試験

CIではStyle-Bert-VITS2推論環境での音声生成までは行いません。

Issue #12を完了扱いにする前に、Style-Bert-VITS2 2.7.0互換環境で以下を確認します。

1. Exporter成果物をmodel_assetsへ配置できる
2. config.json / model / style_vectors.npyが読み込まれる
3. 対象speaker/styleを選択できる
4. 任意の日本語文章を入力できる
5. 音声生成が成功する

実推論確認が終わるまでIssue #12はcloseしません。
