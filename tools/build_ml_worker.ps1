param(
    [string]$OutputDirectory = "artifacts/ml-worker",
    [switch]$PrepareModels
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$worker = Join-Path $repo "src/process/YourSinger.Process/processing/ml/worker"
$output = Join-Path $repo $OutputDirectory
$temp = Join-Path $repo "artifacts/_ml-worker-build"

Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $temp, $output | Out-Null

python -m pip install --upgrade pip
$workerExtra = "$worker[phoneme-supplement]"
python -m pip install $workerExtra "pyinstaller==6.16.0"

$entry = Join-Path $temp "worker_entry.py"
@'
from your_singer_ml.main import main
main()
'@ | Set-Content -Encoding UTF8 $entry

$arguments = @(
    "--noconfirm", "--clean", "--onedir",
    "--name", "YourSinger.ML",
    "--collect-all", "style_bert_vits2",
    "--collect-all", "speechbrain",
    "--collect-all", "faster_whisper",
    "--collect-all", "ctranslate2",
    "--collect-all", "transformers",
    "--collect-all", "tokenizers",
    "--collect-data", "pyopenjtalk",
    "--copy-metadata", "style-bert-vits2",
    "--copy-metadata", "speechbrain",
    "--copy-metadata", "faster-whisper",
    "--copy-metadata", "pyopenjtalk",
    "--distpath", (Join-Path $temp "dist"),
    "--workpath", (Join-Path $temp "work"),
    "--specpath", (Join-Path $temp "spec"),
    $entry
)
python -m PyInstaller @arguments

$dist = Join-Path $temp "dist/YourSinger.ML"
Copy-Item (Join-Path $dist "*") $output -Recurse -Force

if ($PrepareModels) {
    python -m pip install "huggingface_hub==0.35.3"
    $lock = Join-Path $worker "resources/phoneme-supplement.lock.json"
    $models = Join-Path $output "models/phoneme-supplement"
    python (Join-Path $repo "tools/prepare_phoneme_supplement_resources.py") --lock $lock --output $models
}

Write-Host "ML worker bundle: $output"
