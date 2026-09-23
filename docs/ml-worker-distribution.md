# MLワーカー配布設計

## 目的

利用者にPythonやpipをインストールさせず、YourSinger.exeから内部のworkers/YourSinger.ML.exeを起動する。
音素補完用の補助モデルも利用者環境でネットワーク取得せず、配布側が固定revisionで準備する。

## 固定する音素補完資源

固定値は src/process/YourSinger.Process/processing/ml/worker/resources/phoneme-supplement.lock.json に置く。

- Style-Bert-VITS2 runtime: 2.7.0 / 公式tag commit `d8148f3090ee5038ca7b4e4b327116c64467f952`
- faster-whisper runtime: 1.1.1
- SpeechBrain runtime: 1.1.1
- pyopenjtalk runtime: 0.4.1
- Japanese BERT: ku-nlp/deberta-v2-large-japanese-char-wwm / dfd9b4461979b281b3b9f162e50066cc88f08ed3
- ASR: Systran/faster-whisper-small / 536b0662742c02347bc0e980a01041f333bce120
- speaker: speechbrain/spkrec-ecapa-voxceleb / 0f99f2d0ebe89ac095bcc5903c4dd8f72b367286

モデル資源のライセンス表示・帰属表示は公開配布前に確認する。
Japanese BERTはCC-BY-SA-4.0、ASRモデルはMIT、SpeechBrain話者モデルはApache-2.0。
Style-Bert-VITS2本体はAGPL-3.0なので、公開配布物のライセンス構成と対応ソースの提供方法をリリース工程で確認する。

## モデル資源の準備

開発・リリース側だけで prepare_phoneme_supplement_resources.py を実行する。
各snapshotは固定revisionから必要ファイルだけ取得し、resource-lock.jsonとbundle-manifest.jsonを出力する。
manifestには各ファイルのサイズとSHA-256を保存する。

Style-Bert-VITS2 2.7.0はPyPI配布版として取得できないため、配布ビルドでは公式2.7.0 tagのcommit archiveを直接固定してインストールする。PyPI上の別versionへ暗黙にフォールバックしない。

利用者向けのPython導入手順は作らない。

## ワーカーの構築

Windows配布用は tools/build_ml_worker.ps1 を使う。
PrepareModelsを指定すると固定した3種類の補助モデルもworkers/models/phoneme-supplement相当の場所へ準備する。

PyInstallerのonedir構成を採用し、YourSinger.ML.exeと依存DLLを同じworkers配下に置く。
巨大なPyTorch系依存をonefileへ毎回展開する方式は採用しない。

## 起動前検証

worker command phoneme_supplement_preflight は次を検証する。

- 必須Python packageの版
- resource lockとmanifestの対応版
- 3種類の補助モデルの固定取得元・revision
- manifest掲載ファイルの存在・サイズ
- OpenJTalk辞書
- full_verify=trueの場合は各ファイルのSHA-256

通常画面では巨大モデルの全ハッシュを毎回計算しない。
配布ビルド・リリース検証ではfull_verify=trueを使う。
候補生成時には従来通りモデル一式の内容指紋を記録する。

## GitHub Actions

.github/workflows/package-worker.yml は手動実行専用。
通常のPR CIでGB級のモデルを毎回取得しない。
公開配布候補ではinclude_phoneme_models=trueを指定し、full preflightに成功したartifactだけを次のリリース工程へ渡す。

## 未完了

この配布基盤だけで実話者モデルによる補完品質やGUI E2Eを検証したことにはならない。
公開インストーラの署名、第三者ライセンス全文の同梱確認、配布物サイズ最適化、GPU runtimeの分割はリリース工程の残件。
