<#
  main との差分（コミット済み＋作業中）にあるファイルだけを整形する。
  Markdown/JSON/YAML は Prettier、Python は Ruff。LF で出力する。
  使い方: pwsh tools/dev/format_changed.ps1 [-Check]
#>
param([switch]$Check)

$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..\..'))
$base = (git merge-base HEAD origin/main).Trim()
$changed = @(git diff --name-only --diff-filter=AM $base) + @(git ls-files --others --exclude-standard) | Sort-Object -Unique
$prettierFiles = @($changed | Where-Object { $_ -match '\.(md|json|ya?ml)$' -and (Test-Path $_) })
$pythonFiles = @($changed | Where-Object { $_ -match '\.py$' -and (Test-Path $_) })

if (-not (Test-Path node_modules/.bin)) { npm ci --ignore-scripts --no-audit --no-fund | Out-Null }
$mode = if ($Check) { '--check' } else { '--write' }
if ($prettierFiles) { npx --no-install prettier $mode --end-of-line lf @prettierFiles; if ($LASTEXITCODE) { exit $LASTEXITCODE } }
if ($pythonFiles) {
    # ruff はバージョン固定(ruff.toml)。合わなければ artifacts/format-venv に固定版を用意する
    $python = 'python'
    $probe = python -m ruff format --config ruff.toml --check $pythonFiles[0] 2>&1 | Out-String
    if ($probe -match 'Required version') {
        $venv = 'artifacts/format-venv'
        if (-not (Test-Path "$venv/Scripts/python.exe")) {
            python -m venv $venv
            & "$venv/Scripts/python.exe" -m pip install --quiet -r tools/dev/requirements-format.txt
        }
        $python = "$venv/Scripts/python.exe"
    }
    $ruffMode = if ($Check) { '--check' } else { $null }
    & $python -m ruff format $ruffMode --config ruff.toml @pythonFiles
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
Write-Host "整形対象: Prettier $($prettierFiles.Count) 件 / Ruff $($pythonFiles.Count) 件"
