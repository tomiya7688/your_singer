# DiffSingerの音響学習入力

**質感別の学習設定を追加する設計:** [DiffSingerプリセット実現の詳細設計](design/v1-training/diffsinger-preset-realization.md)。現行`acoustic-training.yaml`はプリセット非依存の基準configである。学習用の条件付きEmbedding、Variance/Pitch教師、Augmentation、Vocoder、重み付きサンプルの対応が揃わないうちは「プリセットを学習できた」としない。

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
3. workerがjob ID・話者ID・fingerprint・補正設定と、アプリが検証した`dataset.json`のハッシュを照合します。
4. workerが音声を一時コピーし、測定時のハッシュと一致したコピーだけをPCMサンプル単位で切り出します。
5. workerが公式CSV、音素辞書、設定の一部と変換記録を一時領域へ保存し、完成後に配置します。

```text
VoiceProject/training/diffsinger/<job-id>/
├─ train_weights.json           # 区間Weight manifest(学習itemと1対1。sha256は dataset.json に記録)
├─ dataset.json                 # v1-10.6-weight-manifest: 元音声内の実測開始・終了時刻を含む
└─ acoustic-input/
   ├─ transcriptions.csv        # name,ph_seq,ph_dur
   ├─ wavs/segment_<hash>.wav    # 実測範囲に対応するPCM16モノラル音声
   ├─ measurements/             # ハッシュを照合した測定記録のコピー
   ├─ dictionary-ja.txt         # 入力に現れた音素だけの恒等対応辞書
   ├─ dataset-config.json       # 公式dictionaries/datasets設定の一部
   └─ provenance.json           # 元segment、サンプル境界、job設定
```

測定記録はハッシュを照合して完成品内にコピーし、`provenance.json`に相対パスとハッシュを記録します。
音声の先頭・末尾だけを除き、各音素長を引き伸ばしたり、音声の途中を詰めたりしません。
サンプル境界への丸めによる音声長の差は最大1サンプルです。
元音声・共通特徴量は変更しません。
音素間の隙間や重なりが1マイクロ秒を超える場合は、入力生成を拒否します。
未解析の隙間へ`SP`などのラベルを付ける処理はありません。

辞書は公式入力を読み込むための音素一覧です。日本語の歌詞を任意の発音へ変換する辞書ではありません。
`dataset-config.json`も完全な学習設定ではありません。学習・検証データの分割は含みません。

## 必要な入力と失敗時の対応

歌唱jobと、それに対応する`v1-10.6-weight-manifest`の`dataset.json`を用意してください。
実測方法は`mfa-japanese-phone-forced-alignment-v1`、音声はPCM16モノラルWAVです。
音素列・開始時刻・終了時刻・音素長は同じ個数で、有限な数値を持つ必要があります。
音声と特徴量は同じVoice Project内に保存してください。領域外の参照やリンク先は拒否します。

旧入力、job不一致、途中の未解析区間、境界の範囲外はエラーになります。
該当区間を再解析し、新しい歌唱jobを作成してください。
同じjobの既存入力は上書きしません。失敗時は元入力と元音声が残ります。
`dataset.json`は64 MiB以内、音声は1 GiB以内が必要です。上限を超える場合は学習対象を分割してください。音声の一時コピーを作業領域に作成し、処理後に削除します。
現在は処理APIまでの実装であり、画面からこの段階だけを実行する操作は接続していません。

## Duration入力(variance binarizer用)

音響学習入力の生成後、worker の `prepare_diffsinger_duration` が `duration-input/` を作ります。公式 v2.5.1 の `variance_binarizer.py` は、duration 予測(`predict_dur=true`)で `name, ph_seq, ph_dur, ph_num` を要求し、`ph_num` の合計が音素数と一致することを検査します(`wavs/<name>.wav` も必要)。

- `ph_num`(語ごとの音素数)は測定値がないため、拍(子音+母音、`N`・`cl`・長音・`SP`は単独)から導出します。導出方式は `provenance.json` の `word_grouping`(`mora-v1`、`measured: false`)に残します。
- 未知の音素、母音のない子音、非有限・ゼロ・負の音素長、音声長との不一致のクリップは、原因付きで `held` に保留します。素材は消しません。採用が0件なら出力を作りません。
- 元音声・音響学習入力は変更せず、`duration-input/` は新規作成のみ(既存なら拒否)です。
- 確認済み(2026-11-07, 合成音声4件): 構築済み bundle の公式 `variance_binarizer`(`predict_dur: true`、`predict_pitch: false`)が、この `duration-input` を受け付け、学習3件・検証1件の `train.data`/`valid.data` を作りました。公式の音素カバレッジ検査は `AP`・`SP` の出現を要求するため、#181 の起動入口(不足が `AP`・`SP` だけの場合に限り許可)を通して実行しています。実素材・実GPU学習は未検証です。
- worker の `binarize_diffsinger_duration` が、`duration-input` から variance 用の設定(`duration-training.yaml`。`predict_dur: true`、`predict_pitch: false`)を作り、#181 の起動入口で公式 binarization を実行します(`binary-duration/` に出力。`train.names.json` と件数を照合し、失敗は `run_failure.json` に残します)。C# 側は `DiffSingerDurationBinarizeService`(設定パスがjob領域内であることを検証)が呼びます。Duration の学習は `DiffSingerTrainerService.TrainDurationAsync` が `train_diffsinger` を別の出力先(`trainer-duration/`)・実験名(`<job>-duration`)で呼びます。解決済み計画は音響モデル用のものを実行可否の関門としてだけ使い、Durationの初期化方式は計画に含まれないため新規学習になります。重み付きサンプラーの使用記録は環境変数 `YOURSINGER_WEIGHT_REPORT` で出力先へ書きます。確認済み(2026-11-07, GTX 1080, 合成音声4件, 共有学習キュー経由): 構築済み bundle で Duration の学習を4ステップ実行し、チェックポイント(`model_ckpt_steps_2/4.ckpt`)が出力されました(損失は合成音声のため意味を持ちません)。この確認で、公式の設定が `configs/base.yaml` を作業ディレクトリ基準で辿るため、学習前に固定ソースの `configs` を出力先へ複製する処理を追加しました(出力先は同梱bundleを汚さないため作業ディレクトリです)。実素材での学習品質・長時間学習・OpenUtau出力は未検証です。

## 公式binarizationの起動と学習設定

worker の `binarize_diffsinger` が、音響学習入力から学習設定を作り、固定した公式 `scripts/binarize.py` を実行します。

- 学習設定 `acoustic-training.yaml`(job 直下)は、固定ソースの `configs/acoustic.yaml` を `base_config` に取り、辞書・データセット・検証分割(`test_prefixes`)・`binary_data_dir` だけを指定します。現行のデータ増幅(augmentation)は、重みmanifestと1対1にするため**無効**です。質感別学習で有効にする場合は、派生データの元segment IDと学習item index/weightの正しい対応・元録音単位のtrain/val分離を実装し検証した後に限ります。
- 検証用の区間は名前順で決定的に選びます(全体の1割、最大5件、学習用は必ず1件以上残します)。
- 起動は `diffsinger_binarize_entry.py` 経由で、公式の item 書き込みを差し替え、学習 item 番号→名前を `binary/train.names.json` に残します。重み付きサンプラー(`weighted-ds-batch-v1`)がこの記録を使います。
- 実行は子孫ごと回収できる形(`run_reclaiming`)で行い、タイムアウト・失敗は `run_failure.json` に残します。item 名の記録が無い、または件数が「入力 − 検証用」と合わないときは失敗にします。
- 公式の音素カバレッジ検査は辞書の `AP`・`SP` が学習データに現れることも要求しますが、本アプリは未測定区間へ `AP`・`SP` を付けません。そのため入口で、不足が `AP`・`SP` だけの場合に限り検査を通します(他の音素の不足は従来どおり失敗)。
- 呼吸・声質・テンションの埋め込みは使わないため、外部モデルを要求する `hnsep: vr` ではなく `hnsep: world` を指定します。`--exp_name` は渡しません(公式が `checkpoints/<名前>/config.yaml` を読み書きし、同梱bundleを汚すため)。
- vocoder の取り扱い(2026-11-07 決定。Codex による一次情報の調査に基づく。再配布条件の最終確認は未了): 配布元は OpenVPI vocoders の `pc-nsf-hifigan-44.1k-hop512-128bin-2025.02` リリース(`pc_nsf_hifigan_44.1k_hop512_128bin_2025.02.zip`)で、ライセンスは CC BY-NC-SA 4.0(帰属表示・同条件での共有が必要で、商用利用不可)です。アーカイブ内 notice の追加条件、サイズ、公式のSHA-256は未確認・未公開です。このため**本アプリは同梱せず**、利用者が公式配布元から取得して `models/vocoder/nsf_hifigan/model.ckpt` に置く方式を既定とします(同梱する場合は、非商用の範囲・表示・同一条件共有をライセンス方針に反映したうえで改めて判断します)。取得後のSHA-256は利用者側の記録として保存します。
- 要確認(実GPUでの音響モデル学習): 同梱 bundle で `train_diffsinger` を実際に起動したところ、設定の読み込みまでは通りましたが、`AcousticTask` が NSF-HiFiGAN vocoder の学習済みモデル(`vocoder_ckpt`)を要求して停止しました。このモデルは bundle に含まれず、本アプリはまだ取得・配布していません(音響学習の前提資源。配布・ライセンスの判断が必要です)。取得するまで、歌唱の音響モデル学習は実機で完走できません。Duration の学習は vocoder を使わないため完走を確認済みです。

- 確認済み(2026-11-07): `python tools/smoke_diffsinger_binarize.py <bundle> <作業領域>` で、構築済み bundle の実際の公式 binarization を合成音声4件で実行し、学習3件・検証1件、`train.names.json` と件数の照合が成功しました。実素材・実GPU・実学習は未検証です。

## 歌唱Trainer専用runtimeの構築

入力変換だけでなく、固定したDiffSinger acoustic Trainerを読み込む独立runtimeを構築できます。
既存のトークruntimeは変更しません。
PowerShellからEドライブの完全なPython 3.12.10 runtimeを指定してください。

構築処理は`tools/prepare_trainer_python.py`でPython本体と標準ライブラリだけを複製し、同梱の`ensurepip`からpipを初期化します。
入力Pythonにある`site-packages`と`Scripts`は引き継がず、複製先のPython版・探索先・初期依存がpipだけであることを確認します。
その後に各Trainerの固定依存を導入し、`pip check`を実行します。入力Pythonのライブラリは変更しません。

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

非公開素材で検証しました。

公式のデータ全体のbinarizationには、辞書の音素被覆と学習・検証集合の条件があります。
項目別の特徴抽出が成功しても、この条件や学習品質が満たされたことにはなりません。
Windows配布スクリプトとpackage-worker CIにDiffSinger専用runtime・固定ソースの同梱確認を追加しました。学習ワーカーは専用Pythonから`scripts/train.py`を起動し、ジョブの出力先へログと公式checkpointを配置します。CIは配布物内のソースcommitとTrainer import結果を確認しますが、GPU演算はローカルGTX 1080でのruntime確認が別途必要です。
完全なbinarization、実学習、duration/pitch用のノート情報、ONNX出力、OpenUtauの実レンダリングは引き続き実装・検証します。固定ソースの学習入口は`scripts/train.py`であり、以前想定していた`tasks/run.py`は存在しません。

私的な音声・歌詞・特徴・生成物はGit無視対象のVoice Projectまたは`artifacts`へ保管してください。
公開用fixtureや配布bundleへは含めません。

### MFA測定記録の由来

歌唱用`dataset.json`の各項目は`alignment_measurement`に、Voice Project相対の測定記録パスと記録・音声のSHA-256を保持します。
アプリは作成時に、音声・記録・音素時刻と固定モデル条件を照合し、不一致を拒否します。
不一致が表示された場合は、共通datasetを再構築してから学習ジョブを作り直してください。
この参照だけで無音のSP分類や息のAP分類が完了したことにはなりません。

MFA測定記録の上限は 16 MiB です。
