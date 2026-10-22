<#
  CIと同じ検証をローカルで順に実行します。
  使い方: pwsh tools/dev/check.ps1 [-SkipDotnet] [-SkipWorker] [-SkipSize]
#>
param(
    [switch]$SkipDotnet,
    [switch]$SkipWorker,
    [switch]$SkipSize
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $repositoryRoot

# numpy が無い環境では artifacts/dev-venv に検証用の仮想環境を用意し、そのPythonパスを返す
function Get-WorkerTestPython {
    python -c 'import numpy' 2>$null
    if ($LASTEXITCODE -eq 0) { return (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
    $venvRoot = Join-Path $repositoryRoot 'artifacts/dev-venv'
    $venvPython = Join-Path $venvRoot 'Scripts/python.exe'
    if (-not (Test-Path $venvPython)) { $venvPython = Join-Path $venvRoot 'bin/python' }
    if (-not (Test-Path $venvPython)) {
        python -m venv $venvRoot
        $venvPython = if (Test-Path (Join-Path $venvRoot 'Scripts/python.exe')) { Join-Path $venvRoot 'Scripts/python.exe' } else { Join-Path $venvRoot 'bin/python' }
        & $venvPython -m pip install --quiet numpy
    }
    return $venvPython
}

$stepResults = [System.Collections.Generic.List[object]]::new()

# 1ステップを実行し、成否を記録する
function Invoke-CheckStep {
    param([string]$StepName, [scriptblock]$StepAction)
    Write-Host "=== $StepName ===" -ForegroundColor Cyan
    $isPassed = $true
    try {
        & $StepAction
        if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { $isPassed = $false }
    } catch {
        Write-Host $_ -ForegroundColor Red
        $isPassed = $false
    }
    $stepResults.Add([pscustomobject]@{ Step = $StepName; Passed = $isPassed })
    $global:LASTEXITCODE = 0
}

if (-not $SkipSize) {
    Invoke-CheckStep 'ファイル行数 (300/500行基準)' { & (Join-Path $PSScriptRoot 'check_file_size.ps1') }
}

if (-not $SkipWorker) {
    $workerRoot = Join-Path $repositoryRoot 'src/process/YourSinger.Process/processing/ml/worker'
    $workerPython = Get-WorkerTestPython
    Invoke-CheckStep 'Pythonワーカー 構文確認' { python -m compileall -q (Join-Path $workerRoot 'your_singer_ml') }
    Invoke-CheckStep 'Pythonワーカー 単体テスト' {
        $env:PYTHONPATH = $workerRoot
        & $workerPython -m unittest discover -s tests/worker
    }
}

if (-not $SkipDotnet) {
    Invoke-CheckStep '.NET アプリビルド' {
        dotnet build src/ui/YourSinger.App/YourSinger.App.csproj --configuration Release --nologo -v q
    }
    Invoke-CheckStep '.NET 回帰テスト' {
        if (-not $env:YOURSINGER_TEST_PYTHON) { $env:YOURSINGER_TEST_PYTHON = if ($workerPython) { $workerPython } else { Get-WorkerTestPython } }
        dotnet test tests/YourSinger.Process.Tests/YourSinger.Process.Tests.csproj --configuration Release --nologo -- RunConfiguration.MaxCpuCount=1
    }
}

Write-Host "`n=== 結果 ===" -ForegroundColor Cyan
$stepResults | ForEach-Object {
    $statusLabel = if ($_.Passed) { 'OK  ' } else { 'FAIL' }
    Write-Host "$statusLabel $($_.Step)" -ForegroundColor $(if ($_.Passed) { 'Green' } else { 'Red' })
}
if ($stepResults | Where-Object { -not $_.Passed }) { exit 1 }
