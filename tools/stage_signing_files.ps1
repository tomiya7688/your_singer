<#
  SignPathへ渡す自社製バイナリの取り出しと、署名済みバイナリの書き戻しを行います。
  配布物全体は数GBあるため、署名依頼には自社製のexe/dllだけを渡します(#62)。
  使い方:
    pwsh tools/stage_signing_files.ps1 -Mode Collect -DistributionDirectory artifacts/windows-distribution -StagingDirectory artifacts/signing-input
    pwsh tools/stage_signing_files.ps1 -Mode Restore -DistributionDirectory artifacts/windows-distribution -StagingDirectory artifacts/signing-output
  Collect は相対パスを保ったまま複製します。Restore は、取り出したものと同じ集合の署名済みファイルだけを書き戻し、過不足があれば失敗します。
#>
param(
    [Parameter(Mandatory)][ValidateSet("Collect", "Restore")][string]$Mode,
    [string]$DistributionDirectory = "artifacts/windows-distribution",
    [Parameter(Mandatory)][string]$StagingDirectory
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "BuildPathSafety.ps1")
. (Join-Path $PSScriptRoot "DistributionBinaries.ps1")
$distribution = Resolve-ArtifactBuildPath $repo $DistributionDirectory
$staging = Resolve-ArtifactBuildPath $repo $StagingDirectory
Assert-IndependentBuildSource -SourceDirectory $distribution -OutputDirectories @($staging)

# { 責務: [Get-RelativeSet: ディレクトリ内の自社製バイナリを、相対パスの昇順で返す] }
function Get-RelativeSet([string]$Root) {
    @(Get-OwnBinaries $Root | ForEach-Object { [IO.Path]::GetRelativePath($Root, $_.FullName).Replace("\", "/") } | Sort-Object)
}

$expected = Get-RelativeSet $distribution
if ($expected.Count -eq 0) { throw "署名対象の自社製バイナリが見つかりません。" }

if ($Mode -eq "Collect") {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    foreach ($relative in $expected) {
        $target = Join-Path $staging $relative
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        Copy-Item (Join-Path $distribution $relative) $target
    }
    Write-Host "Collected $($expected.Count) files: $staging"
    return
}

$signed = Get-RelativeSet $staging
if (($signed -join "`n") -ne ($expected -join "`n")) {
    throw "署名済みファイルの集合が配布物と一致しません。expected=$($expected -join ',') actual=$($signed -join ',')"
}
foreach ($relative in $expected) { Copy-Item (Join-Path $staging $relative) (Join-Path $distribution $relative) -Force }
Write-Host "Restored $($expected.Count) signed files into $distribution"
