<#
  コーディング規約の行数基準を確認します。
  原則300行以内、500行超は分割対象です。
  使い方: pwsh tools/dev/check_file_size.ps1 [-ChangedOnly]
  500行超は終了コード1、301〜500行は警告のみです。
#>
param([switch]$ChangedOnly)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $repositoryRoot

$sourceExtensions = '.cs', '.py', '.axaml', '.ps1'
if ($ChangedOnly) {
    $candidatePaths = @(git diff --name-only HEAD) + @(git ls-files --others --exclude-standard) |
        Where-Object { Test-Path $_ -PathType Leaf }
} else {
    $candidatePaths = git ls-files '*.cs' '*.py' '*.axaml' '*.ps1'
}

$oversizedFiles = foreach ($candidatePath in $candidatePaths) {
    if ($sourceExtensions -notcontains [IO.Path]::GetExtension($candidatePath)) { continue }
    if ($candidatePath -match '(^|/)(bin|obj|tests)/|\.Designer\.cs$') { continue }
    $lineCount = (Get-Content -LiteralPath $candidatePath | Measure-Object -Line).Lines
    if ($lineCount -gt 300) { [pscustomobject]@{ Lines = $lineCount; Path = $candidatePath } }
}

$oversizedFiles | Sort-Object Lines -Descending | ForEach-Object {
    $levelLabel = if ($_.Lines -gt 500) { '要分割' } else { '注意  ' }
    Write-Host ("{0} {1,5} {2}" -f $levelLabel, $_.Lines, $_.Path)
}
if ($oversizedFiles | Where-Object { $_.Lines -gt 500 }) { exit 1 }
