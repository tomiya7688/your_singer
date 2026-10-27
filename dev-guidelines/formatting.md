# 変更ファイルの整形

## 背景と目的

共同作業で資料の書式が揺れないよう、[Prettier公式の導入手順](https://prettier.io/docs/install.html)に従い、開発用のPrettier 3.9.10を完全固定します。アプリの配布・学習依存へは追加しません。
今回の対象はMarkdownと整形基盤のJSON/YAMLです。C#/Python、バイナリ、原文保持が必要なサンプルはこのformatterの対象にしません。他言語を変更するときは既存の標準formatterと管理方法を確認してください。

## 準備と実行

Node.js 22以上（CIは24系）とnpmを用意し、リポジトリ直下で実行してください。
[実行環境](execution.md)で一時領域を設定してから、lockfileで固定した依存を取得してください。

```powershell
npm ci --ignore-scripts
```

今回担当するファイルを明示して、整形と確認を順に実行してください。以下はAGENTSだけを変更した場合です。

```powershell
npm run format -- AGENTS.md
npm run format:check -- AGENTS.md
```

複数ファイルは空白区切りで指定し、空白を含むパスは引用符で囲んでください。`npm run format -- .`のような一括指定で他者のdirty変更を整形しないでください。
整形後に差分・意味・改行を確認し、関連検証、自己レビュー、追加レビューへ進みます。指摘修正後も必要な再整形と再検証を実行します。

## 設定とCI

- `package.json`と`package-lock.json`でツール版を固定します。グローバル版や取得時の最新版へ依存しません。
- `.gitattributes`は今回の資料・整形基盤のcheckout改行をLFに固定します。アプリのコードや外部素材の改行規則は変更しません。
- `.prettierrc.json`はLFを指定し、コード例の内部書換えを無効にします。本文の既存の行分けを保持します。
- `.prettierignore`は生成物、私的素材、cache、vendorを除外します。`.npmrc`はnpmのcacheをGit無視対象の`artifacts/npm-cache`へ置きます。
- `format.yml`はPR・pushの変更ファイルと整形設定を、同じ固定版の`npm run format:check -- <対象>`で検査します。削除済みファイルを除き、CIで書き換えません。
- `.gitattributes`、`.gitignore`、`.prettierignore`、`.npmrc`は対象を定めるプレーンテキスト設定です。Prettierの対応言語として整形しません。

全プロジェクトの正本は`E:/projects/FORMATTER_OPERATIONS.md`です。実行コマンド・版・対象・結果と、未実施の場合の理由・残件を完了記録へ残してください。

## Pythonを変更するとき

[Ruff公式の導入方法](https://docs.astral.sh/ruff/installation/)に沿って、開発用依存だけを`tools/dev/requirements-format.txt`で0.14.2へ固定します。
E:内の開発用Python環境を有効にして、リポジトリ直下で実行してください。
ML Trainerの依存へ追加しないでください。

```powershell
python -m pip install -r tools/dev/requirements-format.txt
npm run format:python -- tests/worker/test_mfa_measurement.py
npm run format:python:check -- tests/worker/test_mfa_measurement.py
```

`ruff.toml`はPython 3.11互換・100文字幅・LFを指定し、異なるRuff版の実行を拒否します。
cacheは`artifacts/ruff-cache`へ保存し、私的素材・生成物・vendorを除外します。
`force-exclude`により明示指定したパスにも除外を適用します。CIはPythonの変更がない場合も`--show-settings`で設定を読み込み、設定だけの変更も検証します。
対象は今回のPythonファイルだけを列挙してください。CIの`format.yml`も変更Pythonファイルだけを同じ固定版で検査します。

## C#を変更するとき

.NET SDK 10.0.401に含まれる`dotnet format whitespace`を固定して使います。通常のビルド用SDKの選択は変更しません。
SDK 10.0.401を用意し、[実行環境](execution.md)の一時領域・NuGetキャッシュを設定してから、リポジトリ直下で対象だけを指定してください。

```powershell
npm run format:csharp -- src/data/YourSinger.Data/models/AlignmentMeasurementReference.cs
npm run format:csharp:check -- src/data/YourSinger.Data/models/AlignmentMeasurementReference.cs
```

スクリプトは対象ファイルのプロジェクトを検出し、復元後に固定SDK内のformatterを実行します。
生成物・vendor・project外を拒否し、CIは変更されたC#ファイルだけを`--check`で検査します。
