<#
  作業開始用: main を最新化して issue ブランチを作ります。
  使い方: pwsh tools/dev/start_work.ps1 -Issue 15 -Name diffsinger-output
  未コミットの変更がある場合は何もせず終了します。
#>
param(
    [Parameter(Mandatory)][int]$Issue,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]*$')][string]$Name
)

$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..\..'))

if (git status --porcelain) {
    Write-Error '未コミットの変更があります。コミットまたは退避してから実行してください。'
    exit 1
}

$branchName = "issue-$Issue-$Name"
git fetch origin --prune
git checkout main
git pull --ff-only origin main
git checkout -b $branchName
Write-Host "作業ブランチ $branchName を main の最新から作成しました。" -ForegroundColor Green
