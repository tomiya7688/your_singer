<#
  PRのCIが完了するまで1回で待ち、条件を満たせばsquashマージして後片付けまで行う。
  条件: CI成功・競合なし。満たさなければ理由を出して終了する（PRへの追記は呼び出し側）。
  使い方: pwsh tools/dev/finish_pr.ps1 -Pr <番号> [-TimeoutMinutes 40]
  途中経過は出さない。呼び出し側はバックグラウンドで1回だけ実行し、結果の1行だけ読む。
#>
param(
    [Parameter(Mandatory)][int]$Pr,
    [int]$TimeoutMinutes = 40
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$summary = $null
while ((Get-Date) -lt $deadline) {
    $json = (gh pr view $Pr --json statusCheckRollup,mergeable,state 2>$null) -join "`n"
    $pr = $null
    if ($LASTEXITCODE -eq 0 -and $json) { try { $pr = $json | ConvertFrom-Json } catch { $pr = $null } }
    if ($pr) {
        if ($pr.state -ne 'OPEN') { Write-Output "#$Pr $($pr.state)"; exit 0 }
        $pending = @($pr.statusCheckRollup | Where-Object { $_.status -ne 'COMPLETED' }).Count
        $failed = @($pr.statusCheckRollup | Where-Object { $_.conclusion -eq 'FAILURE' }).Count
        $summary = "pending=$pending failed=$failed mergeable=$($pr.mergeable)"
        if ($pending -eq 0 -and $pr.mergeable -ne 'UNKNOWN' -and $pr.statusCheckRollup.Count -gt 0) { break }
    }
    Start-Sleep -Seconds 20
}
if ($summary -notmatch 'pending=0 failed=0 mergeable=MERGEABLE') { Write-Output "#$Pr 未マージ: $summary"; exit 1 }
gh pr merge $Pr --squash --delete-branch 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Output "#$Pr マージ失敗"; exit 1 }
Write-Output "#$Pr merged"
