# 実行環境とローカル資源

テスト・Python・.NET・ML・配布ビルドを実行するときだけ参照します。このWindows端末の作業場所は`E:\projects\your_singer`です。実行場所・一時領域・出力先を確認してからプロセスを起動してください。

## 一時領域を実行前に設定する

リポジトリ直下のPowerShellで設定します。新しいシェルには再設定が必要です。CIや別端末では、実際のcheckout直下を使います。

```powershell
$repositoryRoot = (Get-Location).Path
$env:TEMP = Join-Path $repositoryRoot 'artifacts/temp'
$env:TMP = $env:TEMP
$env:TMPDIR = $env:TEMP
$env:PYTHONUTF8 = '1'
$env:PYTHONPYCACHEPREFIX = Join-Path $repositoryRoot 'artifacts/python-bytecode-cache'
$env:PIP_CACHE_DIR = Join-Path $repositoryRoot 'artifacts/pip-cache'
$env:DOTNET_CLI_HOME = Join-Path $repositoryRoot 'artifacts/dotnet-cli-home'
$env:NUGET_PACKAGES = Join-Path $repositoryRoot 'artifacts/nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repositoryRoot 'artifacts/nuget-http-cache'
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
```

既定の`TEMP`へ任せるとC:へ書き込む場合があります。実行するスクリプト自身の環境設定も確認してください。sandboxの権限エラーは対象パスと実行条件を確認し、学習失敗やコード不具合と混同しません。

## Python環境を混ぜない

| 用途 | 確認先・条件 |
| --- | --- |
| worker単体テスト | `PYTHONPATH=src/process/YourSinger.Process/processing/ml/worker`。実行可能なPythonと依存を確認します。`tools/dev/check.ps1`も参照してください。 |
| 一般ML worker | `worker/pyproject.toml`と`tools/build_ml_worker.ps1`が依存・包装の原典です。 |
| Talk Trainer | [トークbundle](../docs/talk-trainer-bundle.md)と専用lockに従います。一般workerへ学習依存を追加しません。 |
| DiffSinger Trainer | [音響学習入力](../docs/diffsinger-acoustic-input.md)と専用lockに従います。Talk環境へ追加インストールしません。 |

runtimeが配置済みか、版と依存が合うかは実行時に検証します。単体テスト用Pythonと、実モデルを読み込むPythonを同一環境と仮定しません。
Pythonの取得・依存導入・配布構築では[Python信頼ストア](../docs/python-trust-store.md)も確認し、固定版・最低版と実際の証明書設定を原典の検証処理で照合します。

## 非公開データと長時間処理

- 私的音声・歌詞・特徴・モデル・生成音声は`artifacts/`または`local-projects/`などのGit無視対象に保管します。公開fixture・配布物・PRのログへ含めません。
- 前処理、特徴抽出、binarization、学習、推論、GUI、OpenUtauレンダリングは別の確認項目です。importやCUDA演算の成功だけで学習成功にしません。
- 長時間のビルドはE:内のログへstdout/stderr・終了コードを保存し、工程・最終更新・プロセス状態から進捗を判断します。無出力だけで停止と断定しません。
- 生成途中の配布先を完成品として再利用しません。削除・再構築前に絶対パスと入力資源との独立性を確認します。削除対象の共通検証は`tools/BuildPathSafety.ps1`です。
