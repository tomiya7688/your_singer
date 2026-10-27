param(
    [string]$OutputDirectory = "artifacts/windows-distribution",
    [switch]$IncludePhonemeModels,
    [string]$MfaBundleDirectory = "artifacts/mfa-bundle",
    [string]$TalkTrainerBundleDirectory = "artifacts/style-bert-vits2-bundle",
    [string]$DiffSingerTrainerBundleDirectory = "artifacts/diffsinger-runtime",
    [string]$PythonExecutable = "python"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "BuildPathSafety.ps1")
$output = Resolve-ArtifactBuildPath $repo $OutputDirectory
$workerOutput = Resolve-ArtifactBuildPath $repo "artifacts/_windows-worker"
$appOutput = Resolve-ArtifactBuildPath $repo "artifacts/_windows-app"
$mfaBundle = [IO.Path]::GetFullPath($MfaBundleDirectory, $repo)
Assert-IndependentBuildSource $mfaBundle @($output, $workerOutput, $appOutput)
$talkTrainerBundle = [IO.Path]::GetFullPath($TalkTrainerBundleDirectory, $repo)
Assert-IndependentBuildSource $talkTrainerBundle @($output, $workerOutput, $appOutput)
$diffSingerTrainerBundle = [IO.Path]::GetFullPath($DiffSingerTrainerBundleDirectory, $repo)
Assert-IndependentBuildSource $diffSingerTrainerBundle @($output, $workerOutput, $appOutput)
$sourcePython = (Get-Command $PythonExecutable -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
Assert-IndependentBuildSource (Split-Path -Parent $sourcePython) @($output, $workerOutput, $appOutput)
$env:TEMP = Join-Path $repo "artifacts/temp"
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $repo "artifacts/dotnet-cli-home"
$env:NUGET_PACKAGES = Join-Path $repo "artifacts/nuget-packages"
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repo "artifacts/nuget-http-cache"
$env:LOCALAPPDATA = Join-Path $repo "artifacts/local-app-data"
$env:APPDATA = Join-Path $repo "artifacts/app-data"
$env:AVALONIA_TELEMETRY_OPTOUT = "1"
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

# 固定資源の不足がある場合は既存の配布物を消す前に失敗する。
& $sourcePython (Join-Path $PSScriptRoot 'prepare_mfa_bundle.py') --output $mfaBundle --verify-only
if ($LASTEXITCODE -ne 0) { throw '固定版MFA bundleを構築してから配布物を作成してください。' }
& $sourcePython (Join-Path $PSScriptRoot 'prepare_style_bert_vits2_bundle.py') --output $talkTrainerBundle --verify-only
if ($LASTEXITCODE -ne 0) { throw '固定版トークTrainer bundleを構築してから配布物を作成してください。' }
foreach ($relativePath in @('runtime/python.exe', 'source/scripts/train.py', 'bundle-manifest.json', 'diffsinger-source.lock.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $diffSingerTrainerBundle $relativePath) -PathType Leaf)) {
        throw "固定版DiffSinger Trainer bundleが不足しています: $relativePath"
    }
}

Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $workerOutput -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $appOutput -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $output | Out-Null

if ($IncludePhonemeModels) {
    & (Join-Path $repo "tools/build_ml_worker.ps1") -OutputDirectory "artifacts/_windows-worker" -PrepareModels -MfaBundleDirectory $mfaBundle -PythonExecutable $sourcePython
} else {
    & (Join-Path $repo "tools/build_ml_worker.ps1") -OutputDirectory "artifacts/_windows-worker" -MfaBundleDirectory $mfaBundle -PythonExecutable $sourcePython
}
if ($LASTEXITCODE -ne 0) { throw "MLワーカーの構築に失敗しました。" }

dotnet publish (Join-Path $repo "src/ui/YourSinger.App/YourSinger.App.csproj") --configuration Release --runtime win-x64 --self-contained true --output $appOutput -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Your Singer本体の自己完結publishに失敗しました。" }

Copy-Item (Join-Path $appOutput "*") $output -Recurse -Force
$workers = Join-Path $output "workers"
New-Item -ItemType Directory -Path $workers -Force | Out-Null
Copy-Item (Join-Path $workerOutput "*") $workers -Recurse -Force
Copy-Item -LiteralPath $talkTrainerBundle -Destination (Join-Path $workers 'style-bert-vits2') -Recurse
Copy-Item -LiteralPath $diffSingerTrainerBundle -Destination (Join-Path $workers 'diffsinger') -Recurse
Copy-Item (Join-Path $repo "LICENSE") $output -Force
Copy-Item (Join-Path $repo "THIRD_PARTY_NOTICES.md") $output -Force
Copy-Item (Join-Path $repo "docs/license-policy.md") (Join-Path $output "LICENSE-POLICY-ja.md") -Force

$required = @(
    (Join-Path $output "YourSinger.exe"),
    (Join-Path $output "coreclr.dll"),
    (Join-Path $workers "YourSinger.ML.exe"),
    (Join-Path $workers "mfa/runtime/python.exe"),
    (Join-Path $workers "mfa/models/japanese_mfa.zip"),
    (Join-Path $workers "mfa/MODEL-NOTICE.txt"),
    (Join-Path $workers "style-bert-vits2/runtime/python.exe"),
    (Join-Path $workers "style-bert-vits2/bundle-manifest.json"),
    (Join-Path $workers "style-bert-vits2/resource-lock.json"),
    (Join-Path $workers "style-bert-vits2/MODEL-NOTICES.json"),
    (Join-Path $workers "diffsinger/runtime/python.exe"),
    (Join-Path $workers "diffsinger/source/scripts/train.py"),
    (Join-Path $workers "diffsinger/bundle-manifest.json"),
    (Join-Path $workers "diffsinger/diffsinger-source.lock.json"),
    (Join-Path $output "LICENSE"),
    (Join-Path $output "THIRD_PARTY_NOTICES.md")
)
foreach ($path in $required) {
    if (-not (Test-Path $path -PathType Leaf)) { throw "配布物の必須ファイルがありません: $path" }
}

$rootExecutables = @(Get-ChildItem $output -File -Filter "*.exe")
if (-not ($rootExecutables.Name -contains "YourSinger.exe")) {
    throw "公開起動点のYourSinger.exeがありません。"
}
# .NET自己完結runtimeが内部診断用createdump.exeを同階層へ配置することがある。
# ユーザー向け起動点として扱わず、それ以外の未知のEXEだけを拒否する。
$allowedRuntimeExecutables = @("createdump.exe")
$unexpectedExecutables = @($rootExecutables | Where-Object {
    $_.Name -ne "YourSinger.exe" -and $_.Name -notin $allowedRuntimeExecutables
})
if ($unexpectedExecutables.Count -gt 0) {
    throw "公開ルートに想定外のEXEがあります: $($unexpectedExecutables.Name -join ', ')"
}
if (Test-Path (Join-Path $output "python.exe")) {
    throw "利用者向け配布ルートへPython実行ファイルを公開しません。"
}

# ファイル一覧とhashは、署名後にも使う共通スクリプトで書き込む。
@{
    schema_version = 1
    runtime = "win-x64"
    self_contained_dotnet = $true
    public_entrypoint = "YourSinger.exe"
    worker_entrypoint = "workers/YourSinger.ML.exe"
    includes_phoneme_models = [bool]$IncludePhonemeModels
    includes_talk_trainer = $true
    includes_diffsinger_trainer = $true
    files = @()
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $output "distribution-manifest.json")
& (Join-Path $PSScriptRoot "update_distribution_manifest.ps1") -DistributionDirectory $OutputDirectory

Write-Host "Windows distribution: $output"
