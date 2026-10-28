<#
  公開配布の前に、成果物の署名とチェックサムを検証するゲートです。
  使い方:
    pwsh tools/verify_distribution_signing.ps1 -ReleaseDirectory artifacts/release
    pwsh tools/verify_distribution_signing.ps1 -ReleaseDirectory artifacts/release -DistributionDirectory artifacts/windows-distribution -RequireAuthenticode -ExpectedSignerSubject "SignPath Foundation"
  -RequireAuthenticode を付けると、自社製のexe/dllに、timestamp付きの有効なAuthenticode署名が無い場合に失敗します。
  このとき -ExpectedSignerSubject(証明書のSubjectに含まれるべき文字列)も必須です。他社の有効な署名を通さないためです。
  -AllowSelfSigned は開発段階の自己署名(信頼されていない自己発行の証明書)を許可します。公開リリースでは使わないでください。
  -SkipArchive はZIPとSHA256SUMSの照合を省き、配布フォルダの署名だけを検証します。
  -RequireRfc3161 を付けると、timestampが従来方式ではなくRFC 3161方式であることも確認します(公開リリースで使います)。
#>
param(
    [string]$ReleaseDirectory,
    [string]$DistributionDirectory,
    [string]$ArchiveName = "YourSinger-windows-x64.zip",
    [switch]$RequireAuthenticode,
    [string]$ExpectedSignerSubject,
    # 指定した場合、署名者の証明書のサムプリントも固定する(開発段階の自己署名で使う)。
    [string]$ExpectedSignerThumbprint,
    [switch]$AllowSelfSigned,
    # 公開リリース用: timestampがRFC 3161方式であることを必須にする。
    [switch]$RequireRfc3161,
    [switch]$SkipArchive
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "DistributionBinaries.ps1")

# { 責務: [Test-Rfc3161Timestamp: PEのAuthenticode署名に、RFC 3161方式のtimestampが付いているか返す] }
function Test-Rfc3161Timestamp([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $optionalHeader = $peOffset + 24
        $stream.Position = $optionalHeader
        # PE32は96、PE32+は112バイト目からデータディレクトリが始まり、5番目(index 4)が証明書テーブル。
        $directoryStart = if ($reader.ReadUInt16() -eq 0x20B) { 112 } else { 96 }
        $stream.Position = $optionalHeader + $directoryStart + 32
        $tableOffset = $reader.ReadUInt32()
        $tableSize = $reader.ReadUInt32()
        if ($tableOffset -eq 0 -or $tableSize -le 8) { return $false }
        $stream.Position = $tableOffset
        $length = $reader.ReadUInt32()
        [void]$reader.ReadUInt32()
        $pkcs7 = $reader.ReadBytes([int]$length - 8)
    } finally { $stream.Dispose() }
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode($pkcs7)
    # 1.3.6.1.4.1.311.3.3.1 はRFC 3161のtimestamp、1.2.840.113549.1.9.6 は従来方式のcounter signature。
    $unsigned = $cms.SignerInfos[0].UnsignedAttributes | ForEach-Object { $_.Oid.Value }
    return $unsigned -contains "1.3.6.1.4.1.311.3.3.1"
}

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
    # -like はSubject中の * や [ をワイルドカードとして扱うため、文字列として含むかで判定する。
    if (-not $signer.Subject.Contains($ExpectedSignerSubject)) { return "$($Binary.FullName): 想定外の署名者です ($($signer.Subject))" }
    if ($RequireRfc3161 -and -not (Test-Rfc3161Timestamp $Binary.FullName)) { return "$($Binary.FullName): timestampがRFC 3161方式ではありません" }
    if ($ExpectedSignerThumbprint -and $signer.Thumbprint -ne $ExpectedSignerThumbprint) { return "$($Binary.FullName): 想定外の証明書です ($($signer.Thumbprint))" }
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
$ownBinaries = Get-OwnBinaries $distribution
if ($ownBinaries.Count -eq 0) { throw "署名対象の自社製バイナリが見つかりません。" }
$failures = foreach ($binary in $ownBinaries) { Get-SignatureFailure $binary }
if ($failures) { throw ("Authenticode検証に失敗しました:`n" + ($failures -join "`n")) }
$mode = if ($AllowSelfSigned) { "自己署名を許可" } else { "信頼された署名のみ" }
Write-Host "Authenticode OK: $($ownBinaries.Count) files ($mode)"
