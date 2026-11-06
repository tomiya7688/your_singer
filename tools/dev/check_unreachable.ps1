<#
  到達不能コードの静的検査(Python: pylint W0101 / PowerShell: 構文木)。
  使い方: pwsh tools/dev/check_unreachable.ps1 [-SelfTest]
  -SelfTest: tools/dev/unreachable_samples の合成例で、検査が検出・非検出を正しく判定するか確認する。
  検出範囲: 同一ブロック内で return/throw/break/continue/exit の直後に続く文だけ。条件によって到達できない文、
            C# は Directory.Build.props の警告エラー(CS0162)に任せる。JavaScript は追跡ファイルが無いため対象外。
  除外: 合成例(unreachable_samples)。生成物・vendor は追跡外のため対象に入らない。
#>
param([switch]$SelfTest)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $repositoryRoot

# 検査用の仮想環境(固定版 pylint)のPythonパスを返す
function Get-PylintPython {
    $venvRoot = Join-Path $repositoryRoot 'artifacts/unreachable-venv'
    $venvPython = Join-Path $venvRoot 'Scripts/python.exe'
    if (-not (Test-Path $venvPython)) { $venvPython = Join-Path $venvRoot 'bin/python' }
    if (-not (Test-Path $venvPython)) {
        python -m venv $venvRoot
        $venvPython = if (Test-Path (Join-Path $venvRoot 'Scripts/python.exe')) { Join-Path $venvRoot 'Scripts/python.exe' } else { Join-Path $venvRoot 'bin/python' }
        & $venvPython -m pip install --quiet -r (Join-Path $PSScriptRoot 'requirements-unreachable.txt')
        if ($LASTEXITCODE -ne 0) { throw 'pylint の導入に失敗しました。' }
    }
    return $venvPython
}

# 指定ファイル群をpylintで検査し、検出件数を返す
function Test-PythonUnreachable {
    param([string[]]$Files)
    if ($Files.Count -eq 0) { return 0 }
    $python = Get-PylintPython
    & $python -m pylint --disable=all --enable=W0101 --score=n --persistent=n @Files
    # pylint は検出があるとビットで終了コードを返す(0=検出なし)。値で判定する。
    return [int]($LASTEXITCODE -ne 0)
}

# 指定ファイルの各文ブロックで、終了文の直後に文があれば報告し、件数を返す
function Test-PowerShellUnreachable {
    param([string[]]$Files)
    $count = 0
    $terminators = @(
        [System.Management.Automation.Language.ReturnStatementAst],
        [System.Management.Automation.Language.ThrowStatementAst],
        [System.Management.Automation.Language.BreakStatementAst],
        [System.Management.Automation.Language.ContinueStatementAst],
        [System.Management.Automation.Language.ExitStatementAst]
    )
    foreach ($file in $Files) {
        $tokens = $null; $errors = $null
        $tree = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $file).Path, [ref]$tokens, [ref]$errors)
        if ($errors.Count -gt 0) { Write-Host "${file}: 構文エラー $($errors[0].Message)" -ForegroundColor Red; $count++; continue }
        $blocks = $tree.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.StatementBlockAst] -or
            $node -is [System.Management.Automation.Language.NamedBlockAst]
        }, $true)
        foreach ($block in $blocks) {
            $statements = @($block.Statements)
            for ($i = 0; $i -lt $statements.Count - 1; $i++) {
                $isTerminator = $false
                foreach ($type in $terminators) { if ($statements[$i] -is $type) { $isTerminator = $true } }
                if ($isTerminator) {
                    $line = $statements[$i + 1].Extent.StartLineNumber
                    Write-Host "${file}:${line}: 到達不能コード(直前の終了文の後)" -ForegroundColor Red
                    $count++
                }
            }
        }
    }
    return $count
}

if ($SelfTest) {
    $samples = Join-Path $PSScriptRoot 'unreachable_samples'
    $failures = @()
    if ((Test-PowerShellUnreachable @("$samples/bad.ps1")) -eq 0) { $failures += 'bad.ps1 が検出されません' }
    if ((Test-PowerShellUnreachable @("$samples/good.ps1")) -ne 0) { $failures += 'good.ps1 が誤検出されました' }
    if ((Test-PythonUnreachable @("$samples/bad.py")) -eq 0) { $failures += 'bad.py が検出されません' }
    if ((Test-PythonUnreachable @("$samples/good.py")) -ne 0) { $failures += 'good.py が誤検出されました' }
    if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Host $_ -ForegroundColor Red }; exit 1 }
    Write-Host '自己検証OK(合成例を正しく判定)'
    exit 0
}

# 追跡ファイルだけを対象にする(生成物・vendor・作業用ファイルは入らない)。合成例は除く。
$tracked = @(git ls-files '*.py' '*.ps1' | Where-Object { $_ -notlike 'tools/dev/unreachable_samples/*' })
$python = @($tracked | Where-Object { $_ -like '*.py' })
$powershell = @($tracked | Where-Object { $_ -like '*.ps1' })
$total = (Test-PythonUnreachable $python) + (Test-PowerShellUnreachable $powershell)
if ($total -ne 0) { Write-Host "到達不能コードの検出あり ($total)" -ForegroundColor Red; exit 1 }
Write-Host "到達不能コードなし (Python $($python.Count) / PowerShell $($powershell.Count) ファイル)"
exit 0
