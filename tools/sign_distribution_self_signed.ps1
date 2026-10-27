<#
  開発段階の配布物を、手元の自己署名証明書(既定: CN=Tomiya7688)でAuthenticode署名します。
  公開リリースではSignPath Foundationで署名します(#62)。自己署名ではWindowsの発行元は「不明」のままです。

  使い方:
    # 初回のみ: 現在のユーザーの証明書ストア(Cert:\CurrentUser\My)へ署名用証明書を作成する
    pwsh tools/sign_distribution_self_signed.ps1 -CreateCertificate
    # 署名して、manifestを更新し、署名を検証する
    pwsh tools/sign_distribution_self_signed.ps1

  証明書の秘密鍵は現在のユーザーの証明書ストアにだけ置きます。PFXを書き出したり、リポジトリへ置いたりしないでください。
#>
param(
    [string]$DistributionDirectory = "artifacts/windows-distribution",
    [string]$Subject = "CN=Tomiya7688",
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$CreateCertificate,
    # テスト用: ストアを使わずに渡す証明書
    [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "BuildPathSafety.ps1")
$distribution = Resolve-ArtifactBuildPath $repo $DistributionDirectory

# { 責務: [Get-SigningCertificate: 署名に使う有効なコード署名証明書を1つ返す] }
function Get-SigningCertificate {
    if ($Certificate) { return $Certificate }
    $now = Get-Date
    $found = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt $now } |
        Sort-Object NotAfter -Descending)
    if ($found.Count -gt 0) { return $found[0] }
    if (-not $CreateCertificate) {
        throw "$Subject のコード署名証明書がありません。初回は -CreateCertificate を付けて実行してください。"
    }
    $created = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation Cert:\CurrentUser\My `
        -HashAlgorithm SHA256 -KeyLength 3072 -NotAfter $now.AddYears(3)
    Write-Host "自己署名証明書を作成しました: $($created.Thumbprint)"
    return $created
}

$signingCertificate = Get-SigningCertificate
# 検証スクリプトと同じ規則で、自社製バイナリだけを署名する。
$targets = @(Get-ChildItem $distribution -Recurse -File |
    Where-Object { $_.Name -in @("YourSinger.exe", "YourSinger.ML.exe") -or $_.Name -like "YourSinger.*.dll" })
if ($targets.Count -eq 0) { throw "署名対象の自社製バイナリが見つかりません。" }
foreach ($target in $targets) {
    $result = Set-AuthenticodeSignature -LiteralPath $target.FullName -Certificate $signingCertificate `
        -HashAlgorithm SHA256 -TimestampServer $TimestampServer
    # 既存の署名が残っていても成功と誤認しないよう、指定した証明書で署名されたことを確認する。
    if ($result.SignerCertificate.Thumbprint -ne $signingCertificate.Thumbprint) {
        throw "署名に失敗しました: $($target.FullName) ($($result.StatusMessage))"
    }
}
Write-Host "Signed $($targets.Count) files with $Subject"

# 署名でファイルが変わったため、manifestのhashを更新してから検証する。
& (Join-Path $PSScriptRoot "update_distribution_manifest.ps1") -DistributionDirectory $DistributionDirectory
& (Join-Path $PSScriptRoot "verify_distribution_signing.ps1") -DistributionDirectory $distribution -RequireAuthenticode `
    -ExpectedSignerSubject $Subject -AllowSelfSigned -SkipArchive
