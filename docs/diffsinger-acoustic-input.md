# DiffSingerの音響学習入力

## 背景と目的

Issue #11では、共通音声データから実際のDiffSingerが読める学習入力を生成します。
従来の`dataset.json`には音素長だけがあり、音声の先頭にある未対応部分を区別できませんでした。
開始時刻が0秒ではない素材をそのまま渡すと、公式処理が音素列を0秒から割り当てるため、音声とラベルがずれます。
現在は実測した音素の開始・終了時刻も保存し、対応する範囲だけを音響学習入力へ切り出します。

入力形式の参照先は[OpenVPI DiffSinger v2.5.1](https://github.com/openvpi/DiffSinger/tree/323a5693245b827caba0ab1a7371fe5ca885b061)です。
commitは`323a5693245b827caba0ab1a7371fe5ca885b061`です。
取得元、アーカイブのSHA-256、入力処理と設定のファイルハッシュは`diffsinger-source.lock.json`へ記録します。
この固定は音響入力の形式を対象とし、OpenUtau 0.1.565での生成モデルの互換性は未確認です。

## アプリが生成するもの

1. `DiffSingerDatasetBuilder.BuildAsync`が、歌唱jobの選択区間と実測alignmentを再検証します。
2. `DiffSingerAcousticInputService.PrepareAsync`が、ML workerの`prepare_diffsinger_acoustic`を呼び出します。
3. workerがjob ID・話者ID・fingerprint・補正設定を照合します。
4. workerが音素列の最初の開始時刻から最後の終了時刻までをPCMサンプル単位で切り出します。
5. workerが公式CSV、音素辞書、設定の一部と変換記録を一時領域へ保存し、完成後に配置します。

```text
VoiceProject/training/diffsinger/<job-id>/
├─ dataset.json                 # v1-10.4: 元音声内の実測開始・終了時刻を含む
└─ acoustic-input/
   ├─ transcriptions.csv        # name,ph_seq,ph_dur
   ├─ wavs/segment_<hash>.wav    # 実測範囲に対応するPCM16モノラル音声
   ├─ dictionary-ja.txt         # 入力に現れた音素だけの恒等対応辞書
   ├─ dataset-config.json       # 公式dictionaries/datasets設定の一部
   └─ provenance.json           # 元segment、サンプル境界、job設定
```

音声の先頭・末尾だけを除き、各音素長を引き伸ばしたり、音声の途中を詰めたりしません。
サンプル境界への丸めによる音声長の差は最大1サンプルです。
元音声・共通特徴量は変更しません。
音素間の隙間や重なりが1マイクロ秒を超える場合は、入力生成を拒否します。
未解析の隙間へ`SP`などのラベルを付ける処理はありません。

辞書は公式入力を読み込むための音素一覧です。日本語の歌詞を任意の発音へ変換する辞書ではありません。
`dataset-config.json`も完全な学習設定ではありません。学習・検証データの分割は含みません。

## 必要な入力と失敗時の対応

歌唱jobと、それに対応する`v1-10.4`の`dataset.json`を用意してください。
実測方法は`mfa-japanese-phone-forced-alignment-v1`、音声はPCM16モノラルWAVです。
音素列・開始時刻・終了時刻・音素長は同じ個数で、有限な数値を持つ必要があります。
音声と特徴量は同じVoice Project内に保存してください。領域外の参照やリンク先は拒否します。

旧入力、job不一致、途中の未解析区間、境界の範囲外はエラーになります。
該当区間を再解析し、新しい歌唱jobを作成してください。
同じjobの既存入力は上書きしません。失敗時は元入力と元音声が残ります。
現在は処理APIまでの実装であり、画面からこの段階だけを実行する操作は接続していません。

## 歌唱Trainer専用runtimeの構築

入力変換だけでなく、固定したDiffSinger acoustic Trainerを読み込む独立runtimeを構築できます。
既存のトークruntimeは変更しません。
PowerShellからEドライブの完全なPython 3.12.10 runtimeを指定してください。

```powershell
python tools/prepare_diffsinger_source.py
./tools/build_diffsinger_runtime.ps1 `
  -PythonRuntimeDirectory artifacts/private-ml-runtime/python312
```

構築先は`artifacts/diffsinger-runtime`です。TrainerのPythonは`runtime/`、固定した公式ソースは`source/`へ配置します。
依存をインストールし、`training.acoustic_task`の読み込みとCUDA情報を確認してから完成フォルダを配置します。
依存解決または確認に失敗した場合は未完成runtimeを構築先へ公開しません。

このruntimeにはDiffSingerの学習用依存を含め、別環境を要求するONNX変換依存は含めません。
vocoderの事前学習重み、RMVPEやVocal Removerモデルも取得しません。
学習設定でvocoderを使う場合、各モデルの取得元と配布ライセンスを別に確認してください。

## 公式コードによるローカル検証

開発時は、固定ソースと検証用依存をEドライブの`artifacts`へ配置してください。
固定ソースの取得には`python tools/prepare_diffsinger_source.py`を使ってください。
配置済みソースとZIPの照合には`--verify-only`を指定してください。既存の展開先は上書きしません。
検証用の追加依存は`tools/diffsinger-acoustic-check-requirements.txt`に固定します。
トークTrainerのPythonへ追加インストールせず、別ディレクトリへ`--target`で配置します。
これは項目別の特徴抽出を確認する構成であり、歌唱Trainerの配布runtimeではありません。

```powershell
$env:PYTHONPATH = "$PWD/artifacts/diffsinger-check/deps"
$env:PYTHONPYCACHEPREFIX = "$PWD/artifacts/diffsinger-check/bytecode"
$env:TEMP = "$PWD/artifacts/diffsinger-check/temp"
$env:TMP = $env:TEMP
& artifacts/style-bert-vits2-bundle/runtime/python.exe tools/probe_diffsinger_acoustic.py `
  --source artifacts/diffsinger-source/DiffSinger-323a5693245b827caba0ab1a7371fe5ca885b061 `
  --raw-data '<Voice Project>/training/diffsinger/<job-id>/acoustic-input' `
  --output artifacts/diffsinger-check/official-features
```

`--output`には未作成のディレクトリを指定してください。
このツールは公式の`load_meta_data`と`process_item`を呼び出し、mel・F0・音素対応の長さと有限性を確認します。
公式コードのキャッシュ、作業ファイル、特徴量とログはEドライブ内に保存します。
共通データにあるF0は時刻間隔と定義が異なるため、公式処理へそのまま流用しません。
実測音素境界を再利用し、音響モデル用のmelとF0を公式コードで生成します。

## 検証範囲と残りの作業

2026-10-18に、非公開の実測歌唱2区間・26音素をC#のBuilderと入力変換API経由で変換しました。
GTX 1080上の公式`process_item`で、それぞれ58・93フレームの128次元mel、F0、音素対応を生成しました。
全特徴量の長さ、有限性、音素番号の範囲を確認しました。F0は公式の無声補間後の系列であり、有声判定や音高精度の合格は示しません。
Python 79件、.NET 148件のテストが通過しました。

この小さな入力では、辞書へ自動追加される`AP`・`SP`の2音素が未観測でした。
公式のデータ全体のbinarizationには、辞書の音素被覆と学習・検証集合の条件があります。
項目別の特徴抽出が成功しても、この条件や学習品質が満たされたことにはなりません。
Windows配布スクリプトとpackage-worker CIにDiffSinger専用runtime・固定ソースの同梱確認を追加しました。学習ワーカーは専用Pythonから`scripts/train.py`を起動し、ジョブの出力先へログと公式checkpointを配置します。CIは配布物内のソースcommitとTrainer import結果を確認しますが、GPU演算はローカルGTX 1080でのruntime確認が別途必要です。
完全なbinarization、実学習、duration/pitch用のノート情報、ONNX出力、OpenUtauの実レンダリングは引き続き実装・検証します。固定ソースの学習入口は`scripts/train.py`であり、以前想定していた`tasks/run.py`は存在しません。

私的な音声・歌詞・特徴・生成物はGit無視対象のVoice Projectまたは`artifacts`へ保管してください。
公開用fixtureや配布bundleへは含めません。

