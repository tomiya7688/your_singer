# v1-10 実装設計: OpenUtau / DiffSinger歌唱モデル生成・Exporter

## 対象

- Issue #11
- OpenUtau 0.1.565 をv1互換基準として固定する
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
│  ├─ dsdict.yaml
│  ├─ linguistic.onnx
│  └─ dur.onnx
├─ dspitch/          # 任意。dsconfig/phonemes/dsdict/linguistic/pitchモデルを含む
├─ dsvariance/       # 任意。dsconfig/phonemes/dsdict/linguistic/varianceモデルを含む
└─ dsvocoder/        # 必須
   ├─ vocoder.yaml
   └─ <vocoder model>
```

## Dataset Builder

Universal Voice Datasetとv1-09のSinging学習jobを入力にする。

学習入力には以下を保存する。

- audio path
- transcript
- phoneme列
- phoneme duration
- 元音声内の実測phoneme開始・終了時刻
- F0 feature path
- energy feature path
- mean F0
- pitch reliability
- dataset fingerprint
- 自動補完・補正ON/OFF

実測音素境界を再利用します。音響モデルが要求するmel・F0は時刻間隔と定義が異なるため、固定した公式コードで生成します。
音響入力は[DiffSingerの音響学習入力](diffsinger-acoustic-input.md)を参照してください。

DiffSingerのdurationは音素区間長から作るため、`mfa-japanese-phone-forced-alignment-v1` で音素単位にforced alignmentされたsegmentだけを受け付ける。ASR単語区間内の按分、旧segment全体の均等割り、文字起こし修正後の未再解析データは入力生成時に拒否する。歌唱ジョブ登録時と入力生成時に同じ検証を行い、有限な信頼度・時刻、正の音素長、音素順の逆転や重なりがないことも確認する。MFA backendは[専用bundleと区間別発音辞書](forced-alignment.md)を使い、対応できる音素列の実測時刻を生成する。歌詞・境界精度の確認と実Trainerによる学習完走は残っている。

### Forced alignment backendの採用条件

Montreal Forced Aligner（MFA）の公式配布物には[日本語モデル `japanese_mfa` のモデルカード](https://huggingface.co/MontrealCorpusTools/japanese_mfa)がある。対象は日本語音声で、HMM-GMM方式とCC BY 4.0が記載されている。本実装では更新されるモデルカードとは分けて、v3.0.0の配布ファイルをSHA-256で固定する。モデルカードだけで歌声に対する境界精度を保証しない。

backendと配布資源は、以下の条件を満たす必要がある。

- MFA本体と日本語モデルのバージョン、取得元、ライセンス、配布時の帰属表示を固定する。
- MFAと必要なモデルを配布物へ含める。利用者のPATHや初回起動時のネットワーク取得を前提にしない。
- 歌詞からMFA入力を作り、出力された音素列とyour_singerの音素列の対応を明示的に変換する。対応できない音素や不一致がある場合はalignmentを無効にする。
- MFAの終了コード、出力ファイル、時刻範囲、音素列をすべて検証する。全検証後にのみ`mfa-japanese-phone-forced-alignment-v1`を記録する。
- 非公開の歌声テスト素材で境界を確認し、歌唱音声での精度と失敗条件を記録する。日本語音声用モデルという理由だけで歌唱に適合したとは判断しない。

2026-10-14にWindows上で技術検証した。micromambaからMFA 3.4.2、Python 3.12、CPU版Kaldi 5.5.1172をE:内へ解決・導入でき、Condaパッケージの取得量は約413 MBだった。日本語音響モデル、辞書、G2P、tokenizerは合計約135 MBだった。WindowsではConda環境のDLL探索先とMFAの出力時multiprocessing IPCに追加対応が必要だった。

非公開素材で検証しました。

標準日本語辞書のphone列は内部音素列と一致しなかったため、現在のbackendは`MfaPronunciation`の一音素対応表から区間専用辞書を作る。MFAの実測結果が指定したモデル音素列と完全一致した場合だけ、同じ個数の内部音素へラベルを戻す。長音や長子音を機械的に分割する処理は行わず、促音など未対応表現は実測成功にしない。

MFAの[公式導入資料](https://montreal-forced-aligner.readthedocs.io/en/latest/installation.html)はConda環境を案内しており、Python workerへのpip依存追加だけでは配布要件を満たさない。専用Conda環境、日本語音響モデル、Windows用SoundFile wheel、モデル帰属を別bundleへ配置し、配布ビルドの必須資源として検証する。単一ファイル用`align_one`を使うため、以前のcorpus出力時に問題となったmultiprocessingを使わない。PyInstaller包装済みworkerと配布CI上の実行確認は別途必要である。

## 質感プリセット別の学習レシピ（v1必須）

詳細設計: [Presetからの学習方法選択](design/v1-training/preset-recipe-selection.md)、[DiffSingerの質感別学習・表現幅](design/v1-training/diffsinger-preset-realization.md)。

Your Singerのプリセットは「OpenUtau/DiffSingerにプリセットボタンがあるか」ではなく、**ユーザーが指定した質感を生成するために、学習データの役割・重み、Acoustic/Duration/Pitch/Variance、学習時の条件付け・モデル構成、合成時制御、必要な独立checkpointまで変更する設計**とする。

- `classic_synthetic`: 音素・F0・時間長を正確に制御した、意図した電子的音声。音声破壊を模倣しない。
- `natural`/`clean_controlled`: 正しい発音・F0/Durationと本人の声質・自然な遷移のバランスを選ぶ。
- `expressive_complex`: Breathiness/Voicing/Tension等、固定版に実在するEmbeddingを使えるように**実測特徴抽出→Binarization→Acoustic→必要なVariance/Pitch→OpenUtau推論**まで接続。学習なしで一律`true`にするだけでは不合格。
- `soft_breathy`/`powerful`: 声質参照・有効区間・Tension/Voicing等の条件付き学習とモデル差分を検証。
- 連続制御の幅だけでは目標を再現できない場合は、質感ごとに独立したAcoustic variant/声庫を生成する。OpenUtau voice color統合の未確認段階では、別声庫として安全に出力する。

OpenUtau互換ではPITD/DYN以外のBREC/TENC/VOIC/GENC/VELC/PEXP/voice color等は声庫側の対応が前提。使用していない機能を利用可能と誤表示しない。固定公式`configs/acoustic.yaml`/`variance.yaml`の設定を検証し、Energy/Breathiness/Voicing等の相関のある機能を無条件にすべてONにしない。

**現行の制約:** `diffsinger_binarize.py` は重み付きitem同定のためAugmentationを無効にしている。プリセット別のAugmentationを導入するときは**元segment→派生item→train index→weight**の1対1追跡とtrain/val漏洩防止が必須。まだ未実装の機能を実装済みとしない。

また、現行のAcoustic学習はVocoder checkpointを必須としており、利用候補の再配布条件の問題を残す。対応可能な配布資源・互換性を確認するまで製品完成とはしない。

## Trainer連携

DiffSinger本体はProcessへ直接組み込まず、ML workerから同梱Trainerを起動します。

```text
workers/diffsinger/
├─ runtime/python.exe
├─ source/scripts/train.py
├─ source/training/acoustic_task.py
├─ diffsinger-source.lock.json
└─ bundle-manifest.json
```

### 背景と目的

ML workerの実行ファイルはPython環境として再利用できません。また、固定したOpenVPI DiffSinger v2.5.1には旧設計の`tasks/run.py`がなく、学習入口は`source/scripts/train.py`です。Trainer専用Pythonからこの入口を起動し、配布物だけで依存とソースを解決できる構成にします。

### 現在の処理

1. `tools/build_diffsinger_runtime.ps1`が、固定ソース・Python 3.12.10・CUDA版PyTorchと学習依存を専用bundleへ配置します。
2. Windows配布スクリプトがbundleを`workers/diffsinger`へコピーし、runtime・公式入口・固定情報の必須ファイルを確認します。
3. `DiffSingerTrainerService`が歌唱ジョブID、設定ファイル、Voice Project内の出力先をML workerへ渡します。
4. workerが専用Pythonで`source/scripts/train.py --config ... --exp_name ... --reset`を実行します。
5. stdout/stderrは`training/diffsinger/<job-id>/trainer/trainer.log`へ追記し、公式checkpointも同じジョブの出力領域へ保存します。一時ファイルと各ライブラリのcacheもこの領域へ分離します。

`--reset`はDiffSingerの設定読込時に既存`config.yaml`より指定configを優先させます。Trainer出力を削除する引数ではありません。job名はパスに使える文字へ制限し、出力先が同梱bundle内になる要求は拒否します。

### 確認範囲と残り

ローカルでは専用runtimeの依存整合性、Trainer入口のimport、GTX 1080上のCUDA行列演算を確認しました。worker単体テストではbundleパス・job名・cache配置・ログ追記・異常終了を確認しました。package-worker CIには配布先からのTrainer import確認を追加していますが、CI実行と完成配布物の構築結果は未確認です。

実データの完全なbinarization、GPU学習の完走、学習済みモデルのONNX変換、duration/pitch用のノート情報、OpenUtau 0.1.565での実レンダリングは未確認です。実行方法とruntime構築手順は[DiffSingerの音響学習入力](diffsinger-acoustic-input.md)を参照してください。これらを確認するまでIssue #11を完了扱いにしません。

## Exporter

Exporterは実在する学習成果物を要求する。

必須:

- acoustic model
- duration model
- duration linguistic model
- duration用dsdict.yaml
- phoneme一覧
- local vocoder一式（vocoder.yamlを含む）

任意:

- dspitch一式
- dsvariance一式

OpenUtau 0.1.565ではdspitch / dsvarianceの存在判定は各ディレクトリのdsconfig.yamlで行われるため、単体ONNXだけは配置しない。

必須成果物が欠ける場合はexportを失敗させる。

## 検証

`DiffSingerExportValidator` で以下を機械検証する。

- character.yaml
- dsconfig.yaml
- phonemes.txt
- acoustic.onnx
- dsdur/dsconfig.yaml
- dsdur/phonemes.txt
- dsdur/dsdict.yaml
- dsdur/linguistic.onnx
- dsdur/dur.onnx
- dsvocoder/vocoder.yaml
- root dsconfigのphonemes/acoustic/vocoder key
- dspitch / dsvarianceが存在する場合は各dsconfig・phonemes・dsdict・linguisticを検証

## OpenUtau実機試験

CIではOpenUtau GUI上の実レンダリングまでは検証しない。

Issue #11を完了扱いにする前に、OpenUtau 0.1.565実機で以下を確認する。

1. voicebankを導入できる
2. singerとして認識される
3. 歌詞とノートを入力できる
4. DiffSinger rendererで音声生成できる
5. 欠損model/configエラーが出ない

この実機試験が未完了の間はIssue #11をcloseしない。

## 互換性確認メモ

OpenUtau 0.1.565の実装を基準に、duration phonemizerが `dsdur/dsconfig.yaml` の `linguistic` と `dur` を読み、G2P辞書を利用することを確認した。pitch / variance predictorも各サブディレクトリの `dsconfig.yaml`、phoneme定義、linguistic model、辞書を利用するため、Exporterはサブモデルをディレクトリ一式で受け取る。
