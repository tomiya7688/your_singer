param(
    [Parameter(Mandatory = $true)][string]$PythonRuntimeDirectory,
    [string]$OutputDirectory = "artifacts/style-bert-vits2-runtime"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "BuildPathSafety.ps1")
$output = Resolve-ArtifactBuildPath $repo $OutputDirectory
$source = [IO.Path]::GetFullPath($PythonRuntimeDirectory, $repo)
Assert-IndependentBuildSource $source @($output)
if (Test-Path -LiteralPath $output) { throw "専用runtimeの構築先は未作成のフォルダにしてください。" }
if (Test-Path -LiteralPath (Join-Path $source 'pyvenv.cfg')) { throw "venvではなく完全なWindows Python runtimeを指定してください。" }
$sourcePython = Join-Path $source 'python.exe'
if (-not (Test-Path -LiteralPath $sourcePython -PathType Leaf)) { throw "入力Pythonがありません。" }
$env:TEMP = Resolve-ArtifactBuildPath $repo 'artifacts/style-bert-vits2-runtime-temp'
$env:TMP = $env:TEMP
$env:PIP_CACHE_DIR = Resolve-ArtifactBuildPath $repo 'artifacts/pip-cache'
$env:PYTHONPYCACHEPREFIX = Resolve-ArtifactBuildPath $repo 'artifacts/python-bytecode-cache'
$env:PYTHONUTF8 = '1'
Remove-Item Env:PYTHONHOME -ErrorAction SilentlyContinue
Remove-Item Env:PYTHONPATH -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$version = & $sourcePython -c "import platform; print(platform.python_version())"
if ($LASTEXITCODE -ne 0 -or $version.Trim() -ne '3.12.10') { throw "Python 3.12.10が必要です。" }
# 元Pythonの第三者依存を除外し、標準runtimeとpipだけを専用配置する。
& $sourcePython (Join-Path $PSScriptRoot 'prepare_trainer_python.py') --source $source --output $output
if ($LASTEXITCODE -ne 0) { throw "専用Pythonの複製と初期化に失敗しました。" }
$python = Join-Path $output 'python.exe'
& $python -m pip install --no-compile 'torch==2.3.1+cu121' 'torchaudio==2.3.1+cu121' --index-url https://download.pytorch.org/whl/cu121 --report (Join-Path $output 'torch-install-report.json')
if ($LASTEXITCODE -ne 0) { throw "CUDA用PyTorchの取得に失敗しました。" }
& $python -m pip install --no-compile --requirement (Join-Path $PSScriptRoot 'style-bert-vits2-training-requirements.lock.txt') --report (Join-Path $output 'dependencies-install-report.json')
if ($LASTEXITCODE -ne 0) { throw "トーク学習の専用依存を取得できませんでした。" }
& $python -m pip check
if ($LASTEXITCODE -ne 0) { throw "専用依存の整合性が不正です。" }

# 辞書の暗黙取得をビルド時に済ませ、通常利用はオフラインで動かす。
& $python -c "import pyopenjtalk; assert callable(pyopenjtalk.unset_user_dict); pyopenjtalk.g2p('辞書の準備')"
if ($LASTEXITCODE -ne 0) { throw "日本語辞書を専用runtimeへ配置できませんでした。" }
& $python -m pip freeze --all | Set-Content -Encoding utf8 (Join-Path $output 'installed-requirements.txt')
if ($LASTEXITCODE -ne 0) { throw "専用依存の一覧を記録できませんでした。" }
Write-Host "Style-Bert-VITS2 dedicated runtime: $output"
