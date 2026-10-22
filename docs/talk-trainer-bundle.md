# トーク学習用Trainerの同梱

## 背景

Style-Bert-VITS2 2.7.0の学習では、PyTorch・NumPy・日本語辞書APIの版とモデル資源を固定する必要があります。
同じPythonへ混在させると、解析または学習の依存が壊れます。
通常ワーカーもNumPy 1.26.4と辞書対応版へ揃え、通常版pyopenjtalkとのファイル競合を避けます。構築処理は専用venvを使い、既存Pythonのライブラリを変更しません。

## 目的

構築処理は専用Pythonと固定モデルをまとめ、通常利用者にPython・CUDA toolkit・学習ライブラリの手作業導入を要求しない配布物を作ります。
対応するNVIDIA GPUとドライバーは実行端末に必要です。

## 固定する資源

| 資源 | 固定値 |
| --- | --- |
| Style-Bert-VITS2 | 2.7.0 / `d8148f3090ee5038ca7b4e4b327116c64467f952` |
| Windows Python | 3.12.10 |
| PyTorch / torchaudio | 2.3.1+cu121 |
| CUDA用DLL | 12.1対応wheel内の資源 |
| NumPy | 1.26.4 |
| Transformers / Accelerate | 4.44.2 / 0.34.2 |
| 日本語辞書API | pyopenjtalk-dict 0.3.4.dev2 |
| その他のPython依存 | `tools/style-bert-vits2-training-requirements.lock.txt`の全固定版 |
| モデルのリビジョン・サイズ・SHA-256 | `resources/style-bert-vits2-training.lock.json` |

モデルは公式の日本語BERT・事前学習重み・WavLM・スタイル話者モデルを使用します。
構築処理は取得時と完成時にモデルのSHA-256を確認します。
依存名はPythonの配布名として正規化し、ハイフン・アンダースコア・大文字小文字の表記差を同一視します。

## 開発者の構築方法

以下はリポジトリ直下で実行してください。
構築用の完全なWindows Python 3.12.10の保存先を指定してください。venvは使用できません。

構築処理は`tools/prepare_trainer_python.py`でPython本体と標準ライブラリだけを複製し、同梱の`ensurepip`からpipを初期化します。
入力Pythonにある`site-packages`と`Scripts`は引き継がず、複製先のPython版・探索先・初期依存がpipだけであることを確認します。
その後に各Trainerの固定依存を導入し、`pip check`を実行します。入力Pythonのライブラリは変更しません。

作業先・取得cache・一時領域はリポジトリ内の `artifacts/` へ保存されます。

```powershell
./tools/build_style_bert_vits2_runtime.ps1 -PythonRuntimeDirectory E:\tools\Python312
python tools/prepare_style_bert_vits2_bundle.py --runtime artifacts/style-bert-vits2-runtime
./tools/build_windows_distribution.ps1 -TalkTrainerBundleDirectory artifacts/style-bert-vits2-bundle
```

MFAなど他の配布資源も必要です。音素時刻用資源の構築方法は[強制アラインメント](./forced-alignment.md)を参照してください。
CIではPython 3.12.10の配置後に同じTrainer構築手順を実行します。

取得済みモデルは `artifacts/style-bert-vits2-downloads/` にhash付きで保存します。
モデルだけを先に取得する場合は次を実行してください。

```powershell
python tools/prepare_style_bert_vits2_bundle.py --download-only
```

既存bundleの全モデルhashを再確認する場合は次を実行してください。

```powershell
python tools/prepare_style_bert_vits2_bundle.py --output artifacts/style-bert-vits2-bundle --verify-only
```

構築処理は既存の出力フォルダを上書きしません。
再構築する場合は別の `--output` を指定してください。
大容量コピー前とコピー後に専用runtimeの依存を照合し、モデルの実ロード確認が通った一式だけを完成先へ移します。
失敗時のログは `<outputのフォルダ名>-verification/runtime-probe.log` に残します。

## 配布先の配置と起動

配布処理は次を `workers/style-bert-vits2/` へ配置します。

- 専用Python・CUDA用DLL・全Python依存
- 固定版の公式ソースと既定設定
- 日本語BERT・事前学習重み・WavLM・話者モデル
- OpenJTalkの日本語辞書
- 固定資源lock・依存一覧manifest・モデルの出典とライセンス情報
- 移動先からの実ロード確認用 `runtime-probe.py`

利用者は配布物を展開し、`YourSinger.exe` を起動してください。
学習処理の入力・工程・保存先は[トークモデル生成の実装設計](./implementation-v1-11.md)を参照してください。
現在はTrainer処理APIを接続しており、画面からの一連の学習操作は未確認です。

## 検証する内容

`runtime-probe.py` は配置先の専用Pythonで、依存の実際の版・tokenizer・BERT本体・公式話者モデルの読み込みを確認します。BERTは公式と同じ `device_map` 経路で特徴を計算し、実行時のAccelerate不足も検出します。
合成無音入力に対して256次元の有限な特徴が出ることを確認し、GPUがある端末ではCUDA行列演算も行います。
CIのGPUを持たないrunnerでは、GPU利用可否をfalseとして記録します。GPU学習成功とは扱いません。

2026-10-17にGTX 1080と専用PyTorch 2.3.1+cu121のCUDA行列演算を確認しました。
依存変更後のPythonテスト70件と.NETテスト147件も通過しました。
同梱BERTの1024次元特徴と公式話者モデルの256次元特徴を実計算し、配置先でのロードを確認しました。
非公開の歌唱音声2区間では、リサンプル・音素とアクセント生成・BERT特徴・公式スタイル特徴の5工程が通りました。
これは歌唱区間による前処理の接続確認であり、Speech用データや実学習の合格とは扱いません。
実学習・学習済みモデルの任意文章読み上げ・画面操作・CIの実行は別に確認します。

## 公開配布前の確認

公式ソースのAGPL-3.0と辞書部分のLGPLに関するソース・ライセンス本文を一緒に保持します。
日本語BERTはCC-BY-SA-4.0、話者モデルはCC-BY-4.0です。
WavLMのモデルカードが示す[ライセンス参照先](https://github.com/microsoft/UniSpeech/blob/main/LICENSE)はCC-BY-SA-3.0であり、MITとして記録しません。
事前学習重みの[固定版モデルカード](https://huggingface.co/litagin/Style-Bert-VITS2-1.0-base/blob/56b47b34cb2bbc9f750dfe64504bb6e3e213f1fb/README.md)にはライセンスの宣言がありません。
そのため、重みの再配布条件と依存全体のNOTICE確認は未完了としてmanifestへ記録します。
ローカルでの動作確認と、公開配布の確認完了は区別します。

私的な入力音声・歌詞・特徴・学習による生成物は、この構築処理へ含めません。
それらはVoice Projectまたはローカル検証用のGit無視対象へ保管し、公開しません。
