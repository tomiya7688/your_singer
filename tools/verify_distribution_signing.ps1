<#
  公開配布の前に、成果物の署名とチェックサムを検証するゲートです。
  使い方:
    pwsh tools/verify_distribution_signing.ps1 -ReleaseDirectory artifacts/release
    pwsh tools/verify_distribution_signing.ps1 -ReleaseDirectory artifacts/release -DistributionDirectory artifacts/windows-distribution -RequireAuthenticode -ExpectedSignerSubject "SignPath Foundation"
  -RequireAuthenticode を付けると、自社製のexe/dllに、timestamp付きの有効なAuthenticode署名が無い場合に失敗します。
  このとき -ExpectedSignerSubject(証明書のSubjectに含まれるべき文字列)も必須です。他社の有効な署名を通さないためです。
  -AllowSelfSigned は開発段階の自己署名(信頼されていない自己発行の証明書)を許可します。公開リリースでは使わないでください。
  -SkipArchive はZIPとSHA256SUMSの照合を省き、配布フォルダの署名だけを検証します。
  timestampの方式(従来方式かRFC 3161か)はこのスクリプトでは区別できません(#62で対応)。
#>
param(
    [string]$ReleaseDirectory,
    [string]$DistributionDirectory,
    [string]$ArchiveName = "YourSinger-windows-x64.zip",
    [switch]$RequireAuthenticode,
    [string]$ExpectedSignerSubject,
    [switch]$AllowSelfSigned,
    [switch]$SkipArchive
)

$ErrorActionPreference = "Stop"

# { 責務: [Assert-ArchiveChecksum: SHA256SUMSが最終成果物そのものと一致するか確認する] }
function Assert-ArchiveChecksum {
    if (-not $ReleaseDirectory) { throw "-ReleaseDirectory が必要です(照合しない場合は -SkipArchive)。" }
    $release = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReleaseDirectory)
    $archivePath = Join-Path $release $ArchiveName
    $sumsPath = Join-Path $release "SHA256SUMS"
    foreach ($requiredPath in @($archivePath, $sumsPath)) {
        if (-not (Test-Path $requiredPath -PathType Leaf)) { throw "最終成果物がありません: $requiredPath" }
    }
    # ファイル名が完全一致する行だけを採用する(部分一致で別ファイルの行を拾わない)。
    $expectedLine = (Get-Content $sumsPath | Where-Object { ($_ -split '\s+', 2)[1] -replace '^\*', '' -eq $ArchiveName } | Select-Object -First 1)
    if (-not $expectedLine) { throw "SHA256SUMSに$ArchiveNameがありません。" }
    $expectedHash = ($expectedLine -split '\s+')[0].ToLowerInvariant()
    $actualHash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expectedHash -ne $actualHash) { throw "SHA-256が一致しません: expected=$expectedHash actual=$actualHash" }
    Write-Host "SHA-256 OK: $actualHash"
}

# { 責務: [Get-SignatureFailure: 1ファイルの署名が条件を満たさない理由を返す。満たす場合は何も返さない] }
function Get-SignatureFailure([IO.FileInfo]$Binary) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Binary.FullName
    $signer = $signature.SignerCertificate
    if (-not $signer) { return "$($Binary.FullName): 署名がありません ($($signature.Status))" }
    # 自己署名は信頼チェーンが無いため Valid にならない。許可した場合だけ、自己発行の証明書に限って通す。
    $isSelfSigned = $signer.Subject -eq $signer.Issuer
    $isAcceptedStatus = $signature.Status -eq "Valid" -or ($AllowSelfSigned -and $isSelfSigned -and $signature.Status -in @("UnknownError", "NotTrusted"))
    if (-not $isAcceptedStatus) { return "$($Binary.FullName): 署名が有効ではありません ($($signature.Status))" }
    if (-not $signature.TimeStamperCertificate) { return "$($Binary.FullName): timestampがありません" }
    if ($signer.Subject -notlike "*$ExpectedSignerSubject*") { return "$($Binary.FullName): 想定外の署名者です ($($signer.Subject))" }
}

if (-not $SkipArchive) { Assert-ArchiveChecksum }

if (-not $RequireAuthenticode) {
    Write-Warning "Authenticode署名は検証していません。公開リリースには -RequireAuthenticode が必要です。"
    return
}

if (-not $DistributionDirectory) { throw "-RequireAuthenticode には -DistributionDirectory が必要です。" }
if (-not $ExpectedSignerSubject) { throw "-RequireAuthenticode には -ExpectedSignerSubject が必要です。" }
$distribution = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DistributionDirectory)
# 自社製のバイナリだけを対象にする。第三者のruntimeは配布元の署名・固定hashで扱う。
$ownBinaries = @(Get-ChildItem $distribution -Recurse -File |
    Where-Object { $_.Name -in @("YourSinger.exe", "YourSinger.ML.exe") -or $_.Name -like "YourSinger.*.dll" })
if ($ownBinaries.Count -eq 0) { throw "署名対象の自社製バイナリが見つかりません。" }
$failures = foreach ($binary in $ownBinaries) { Get-SignatureFailure $binary }
if ($failures) { throw ("Authenticode検証に失敗しました:`n" + ($failures -join "`n")) }
$mode = if ($AllowSelfSigned) { "自己署名を許可" } else { "信頼された署名のみ" }
Write-Host "Authenticode OK: $($ownBinaries.Count) files ($mode)"
