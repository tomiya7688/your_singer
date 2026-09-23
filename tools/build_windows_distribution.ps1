param(
    [string]$OutputDirectory = "artifacts/windows-distribution",
    [switch]$IncludePhonemeModels
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo $OutputDirectory
$workerOutput = Join-Path $repo "artifacts/_windows-worker"
$appOutput = Join-Path $repo "artifacts/_windows-app"

Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $workerOutput -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $appOutput -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $output | Out-Null

if ($IncludePhonemeModels) {
    & (Join-Path $repo "tools/build_ml_worker.ps1") -OutputDirectory "artifacts/_windows-worker" -PrepareModels
} else {
    & (Join-Path $repo "tools/build_ml_worker.ps1") -OutputDirectory "artifacts/_windows-worker"
}
if ($LASTEXITCODE -ne 0) { throw "MLワーカーの構築に失敗しました。" }

dotnet publish (Join-Path $repo "src/ui/YourSinger.App/YourSinger.App.csproj") --configuration Release --runtime win-x64 --self-contained true --output $appOutput -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Your Singer本体の自己完結publishに失敗しました。" }

Copy-Item (Join-Path $appOutput "*") $output -Recurse -Force
$workers = Join-Path $output "workers"
New-Item -ItemType Directory -Path $workers -Force | Out-Null
Copy-Item (Join-Path $workerOutput "*") $workers -Recurse -Force
Copy-Item (Join-Path $repo "LICENSE") $output -Force
Copy-Item (Join-Path $repo "THIRD_PARTY_NOTICES.md") $output -Force
Copy-Item (Join-Path $repo "docs/license-policy.md") (Join-Path $output "LICENSE-POLICY-ja.md") -Force

$required = @(
    (Join-Path $output "YourSinger.exe"),
    (Join-Path $output "coreclr.dll"),
    (Join-Path $workers "YourSinger.ML.exe"),
    (Join-Path $output "LICENSE"),
    (Join-Path $output "THIRD_PARTY_NOTICES.md")
)
foreach ($path in $required) {
    if (-not (Test-Path $path -PathType Leaf)) { throw "配布物の必須ファイルがありません: $path" }
}

$rootExecutables = @(Get-ChildItem $output -File -Filter "*.exe")
if ($rootExecutables.Count -ne 1 -or $rootExecutables[0].Name -ne "YourSinger.exe") {
    throw "公開ルートの起動EXEはYourSinger.exe 1件だけにしてください。"
}
if (Test-Path (Join-Path $output "python.exe")) {
    throw "利用者向け配布ルートへPython実行ファイルを公開しません。"
}

$manifest = Get-ChildItem $output -File -Recurse | Sort-Object FullName | ForEach-Object {
    [PSCustomObject]@{
        path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace("\", "/")
        size = $_.Length
        sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
@{
    schema_version = 1
    runtime = "win-x64"
    self_contained_dotnet = $true
    public_entrypoint = "YourSinger.exe"
    worker_entrypoint = "workers/YourSinger.ML.exe"
    includes_phoneme_models = [bool]$IncludePhonemeModels
    files = $manifest
} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $output "distribution-manifest.json")

Write-Host "Windows distribution: $output"
