<#
  公開配布の前に、成果物の署名とチェックサムを検証するゲートです。
  使い方:
    pwsh tools/verify_distribution_signing.ps1 -ReleaseDirectory artifacts/release
    pwsh tools/verify_distribution_signing.ps1 -ReleaseDirectory artifacts/release -DistributionDirectory artifacts/windows-distribution -RequireAuthenticode
  -RequireAuthenticode を付けると、自社製のexe/dllにRFC 3161 timestamp付きの有効なAuthenticode署名が無い場合に失敗します。
  公開リリースは必ず -RequireAuthenticode を付けて実行してください。
#>
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [string]$DistributionDirectory,
    [string]$ArchiveName = "YourSinger-windows-x64.zip",
    [switch]$RequireAuthenticode
)

$ErrorActionPreference = "Stop"
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
$archivePath = Join-Path $release $ArchiveName
$sumsPath = Join-Path $release "SHA256SUMS"
foreach ($requiredPath in @($archivePath, $sumsPath)) {
    if (-not (Test-Path $requiredPath -PathType Leaf)) { throw "最終成果物がありません: $requiredPath" }
}

# SHA256SUMSが最終成果物そのものと一致するか確認する。
$expectedLine = (Get-Content $sumsPath | Where-Object { $_ -match [regex]::Escape($ArchiveName) } | Select-Object -First 1)
if (-not $expectedLine) { throw "SHA256SUMSに$ArchiveNameがありません。" }
$expectedHash = ($expectedLine -split '\s+')[0].ToLowerInvariant()
$actualHash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expectedHash -ne $actualHash) { throw "SHA-256が一致しません: expected=$expectedHash actual=$actualHash" }
Write-Host "SHA-256 OK: $actualHash"

if (-not $RequireAuthenticode) {
    Write-Warning "Authenticode署名は検証していません。公開リリースには -RequireAuthenticode が必要です。"
    return
}

if (-not $DistributionDirectory) { throw "-RequireAuthenticode には -DistributionDirectory が必要です。" }
$distribution = [IO.Path]::GetFullPath($DistributionDirectory)
# 自社製のバイナリだけを対象にする。第三者のruntimeは配布元の署名・固定hashで扱う。
$ownBinaries = @(Get-ChildItem $distribution -Recurse -File |
    Where-Object { $_.Name -in @("YourSinger.exe", "YourSinger.ML.exe") -or $_.Name -like "YourSinger.*.dll" })
if ($ownBinaries.Count -eq 0) { throw "署名対象の自社製バイナリが見つかりません。" }
$failures = foreach ($binary in $ownBinaries) {
    $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName
    if ($signature.Status -ne "Valid") { "$($binary.FullName): 署名が有効ではありません ($($signature.Status))" }
    elseif (-not $signature.TimeStamperCertificate) { "$($binary.FullName): RFC 3161 timestampがありません" }
}
if ($failures) { throw ("Authenticode検証に失敗しました:`n" + ($failures -join "`n")) }
Write-Host "Authenticode OK: $($ownBinaries.Count) files"
