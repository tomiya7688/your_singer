param(
    [string]$OutputDirectory = "artifacts/ml-worker",
    [switch]$PrepareModels,
    [string]$MfaBundleDirectory = "artifacts/mfa-bundle",
    [string]$PythonExecutable = "python"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "BuildPathSafety.ps1")
$worker = Join-Path $repo "src/process/YourSinger.Process/processing/ml/worker"
$output = Resolve-ArtifactBuildPath $repo $OutputDirectory
$temp = Resolve-ArtifactBuildPath $repo "artifacts/_ml-worker-build"
$mfaBundle = [IO.Path]::GetFullPath($MfaBundleDirectory, $repo)
$sourcePython = (Get-Command $PythonExecutable -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
Assert-IndependentBuildSource (Split-Path -Parent $sourcePython) @($output, $temp)
Assert-IndependentBuildSource $mfaBundle @($output, $temp)
$env:TEMP = Join-Path $repo "artifacts/temp"
$env:TMP = $env:TEMP
$env:PIP_CACHE_DIR = Join-Path $repo "artifacts/pip-cache"
$env:PYTHONPYCACHEPREFIX = Join-Path $repo "artifacts/python-bytecode-cache"
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$env:HOME = Join-Path $repo "artifacts/build-home"
$env:USERPROFILE = $env:HOME
$env:LOCALAPPDATA = Join-Path $repo "artifacts/local-app-data"
$env:APPDATA = Join-Path $repo "artifacts/app-data"
$env:HF_HOME = Join-Path $repo "artifacts/build-model-cache/huggingface"
$env:TORCH_HOME = Join-Path $repo "artifacts/build-model-cache/torch"
$env:XDG_CACHE_HOME = Join-Path $repo "artifacts/build-model-cache"
$env:MPLCONFIGDIR = Join-Path $repo "artifacts/build-model-cache/matplotlib"
$env:NLTK_DATA = Join-Path $repo "artifacts/build-model-cache/nltk"
$env:PYTHONNOUSERSITE = "1"
$env:MPLBACKEND = "Agg"
Remove-Item Env:PYTHONHOME, Env:PYTHONPATH, Env:OPEN_JTALK_DICT_DIR -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $env:HOME, $env:LOCALAPPDATA, $env:APPDATA, $env:NLTK_DATA | Out-Null
& $sourcePython (Join-Path $PSScriptRoot "prepare_mfa_bundle.py") --output $mfaBundle --verify-only
if ($LASTEXITCODE -ne 0) { throw "固定版MFA bundleを構築してから配布物を作成してください。docs/forced-alignment.mdを参照してください。" }

Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $temp, $output | Out-Null

# 既存のPythonへ解析・補完ライブラリを混在させず、Eドライブ内の構築専用venvへ導入する。
& $sourcePython -m venv (Join-Path $temp "runtime")
if ($LASTEXITCODE -ne 0) { throw "MLワーカー構築専用のPython環境を作成できませんでした。" }
$buildPython = Join-Path $temp "runtime/Scripts/python.exe"
# 証明書インシデント対応(#48): 最低版のpipとcertifiを先に導入する。
& $buildPython -m pip install --upgrade "pip>=26.2.2" "certifi>=2026.10.19"
if ($LASTEXITCODE -ne 0) { throw "pipまたはcertifiの更新に失敗しました。" }
$workerExtra = "$worker[phoneme-supplement]"
& $buildPython -m pip install --no-compile $workerExtra "pyinstaller==6.16.0"
if ($LASTEXITCODE -ne 0) { throw "MLワーカー依存のインストールに失敗しました。" }
& $buildPython -m pip check
if ($LASTEXITCODE -ne 0) { throw "MLワーカー依存の整合性が不正です。" }
& $buildPython (Join-Path $PSScriptRoot "verify_python_trust.py")
if ($LASTEXITCODE -ne 0) { throw "構築用Pythonのpip/certifiが最低版を満たしていません。" }

# pyopenjtalk-dictの辞書は初回利用時に取得されるため、
# 配布物をオフラインで使えるようリリース側で一度展開してから包装する。
& $buildPython -c "import os, pathlib, pyopenjtalk; pyopenjtalk.g2p('テスト'); p=pathlib.Path(os.fsdecode(pyopenjtalk.OPEN_JTALK_DICT_DIR)); print(p); assert p.is_dir()"
if ($LASTEXITCODE -ne 0) { throw "OpenJTalk日本語辞書の配布用展開に失敗しました。" }

$entry = Join-Path $temp "worker_entry.py"
@'
from your_singer_ml.main import main
main()
'@ | Set-Content -Encoding UTF8 $entry

$arguments = @(
    "--noconfirm", "--clean", "--onedir",
    "--name", "YourSinger.ML",
    "--collect-submodules", "your_singer_ml",
    "--collect-all", "demucs",
    "--collect-all", "style_bert_vits2",
    "--collect-all", "accelerate",
    "--collect-all", "speechbrain",
    "--collect-all", "faster_whisper",
    "--collect-all", "ctranslate2",
    "--collect-all", "transformers",
    "--collect-all", "tokenizers",
    "--collect-all", "pyopenjtalk",
    "--copy-metadata", "demucs",
    "--copy-metadata", "style-bert-vits2",
    "--copy-metadata", "speechbrain",
    "--copy-metadata", "faster-whisper",
    "--copy-metadata", "pyopenjtalk-dict",
    "--copy-metadata", "numpy",
    "--copy-metadata", "accelerate",
    "--copy-metadata", "setuptools",
    "--add-data", "$(Join-Path $worker 'your_singer_ml/mfa_entry.py');your_singer_ml",
    "--add-data", "$(Join-Path $worker 'your_singer_ml/style_bert_vits2_entry.py');your_singer_ml",
    "--distpath", (Join-Path $temp "dist"),
    "--workpath", (Join-Path $temp "work"),
    "--specpath", (Join-Path $temp "spec"),
    $entry
)
& $buildPython -m PyInstaller @arguments
if ($LASTEXITCODE -ne 0) { throw "PyInstallerによるワーカー構築に失敗しました。" }

$dist = Join-Path $temp "dist/YourSinger.ML"
Copy-Item (Join-Path $dist "*") $output -Recurse -Force
Copy-Item -LiteralPath $mfaBundle -Destination (Join-Path $output "mfa") -Recurse

if ($PrepareModels) {
    & $buildPython -m pip install --no-compile "huggingface_hub==0.35.3"
    if ($LASTEXITCODE -ne 0) { throw "モデル取得ツールの準備に失敗しました。" }
    $lock = Join-Path $worker "resources/phoneme-supplement.lock.json"
    $models = Join-Path $output "models/phoneme-supplement"
    & $buildPython (Join-Path $repo "tools/prepare_phoneme_supplement_resources.py") --lock $lock --output $models
    if ($LASTEXITCODE -ne 0) { throw "固定モデル資源の準備に失敗しました。" }
}

Write-Host "ML worker bundle: $output"
