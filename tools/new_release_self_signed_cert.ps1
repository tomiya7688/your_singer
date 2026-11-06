<#
  リリース用の自己署名コード署名証明書(既定: CN=Tomiya7688)を作り、GitHubのEnvironment「release」へ登録します(#62)。
  SignPath Foundationの承認が下りるまでの間、タグ実行のワークフローがこの証明書で署名します。
  使い方(リポジトリオーナーが1回だけ実行する。要 gh auth login):
    pwsh tools/new_release_self_signed_cert.ps1 -Repository tomiya7688/your_singer
    pwsh tools/new_release_self_signed_cert.ps1 -Repository tomiya7688/your_singer -DryRun   # 作成と検証だけ。何も登録しない
  秘密鍵とパスワードはメモリ上だけで扱い、ファイルへ書かず、画面にも表示しません。
  登録するもの: Environmentのsecret SELF_SIGN_PFX_BASE64 / SELF_SIGN_PFX_PASSWORD、variable SELF_SIGN_THUMBPRINT。
  自己署名は信頼チェーンを持たないため、Windowsの発行元は「不明」のままです。公開配布の最終解にはしません。
#>
param(
    [Parameter(Mandatory)][string]$Repository,
    [string]$Subject = "CN=Tomiya7688",
    [string]$Environment = "release",
    [int]$ValidYears = 3,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# { 責務: [New-SigningCertificate: コード署名用EKUを持つ自己署名証明書を秘密鍵付きで作る] }
function New-SigningCertificate {
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new($Subject, $rsa, "SHA256", [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $usages = [Security.Cryptography.OidCollection]::new()
    [void]$usages.Add([Security.Cryptography.Oid]::new("1.3.6.1.5.5.7.3.3"))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $false))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears($ValidYears))
}

$certificate = New-SigningCertificate
$password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$pfxBytes = $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)

# ワークフローが読み込める形で出力できていることを、登録の前に確認する。
$loaded = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxBytes, $password)
$isCodeSigning = $loaded.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
    ForEach-Object { $_.EnhancedKeyUsages.Value -contains "1.3.6.1.5.5.7.3.3" }
if (-not $loaded.HasPrivateKey -or -not $isCodeSigning -or $loaded.Subject -ne $Subject) {
    throw "作成した証明書の確認に失敗しました(秘密鍵・コード署名用途・Subject)。"
}
$thumbprint = $loaded.Thumbprint
Write-Host "証明書を作成しました。Subject=$Subject Thumbprint=$thumbprint 有効期限=$($loaded.NotAfter.ToString('yyyy-MM-dd'))"

if ($DryRun) {
    Write-Host "-DryRun のため、GitHubへは何も登録していません。"
    return
}

# Environmentを作り、保護を設定する: 必須レビュアーは実行した本人、デプロイできるのはタグ v* だけ。
# タグ実行のworkflowはタグのコミットの内容で動くため、承認なしに秘密鍵を読ませないための保護です。
$userId = [int](gh api user --jq .id)
if ($LASTEXITCODE -ne 0) { throw "GitHubのユーザーを取得できませんでした。gh auth login を確認してください。" }
$protection = @{
    reviewers = @(@{ type = "User"; id = $userId })
    deployment_branch_policy = @{ protected_branches = $false; custom_branch_policies = $true }
} | ConvertTo-Json -Depth 5
$protection | gh api --method PUT "repos/$Repository/environments/$Environment" --input - --silent
if ($LASTEXITCODE -ne 0) { throw "Environment「$Environment」を作成・保護できませんでした。" }
$existingTagPolicy = gh api "repos/$Repository/environments/$Environment/deployment-branch-policies" --jq '.branch_policies[] | select(.name == "v*" and .type == "tag") | .name'
if (-not $existingTagPolicy) {
    @{ name = "v*"; type = "tag" } | ConvertTo-Json | gh api --method POST "repos/$Repository/environments/$Environment/deployment-branch-policies" --input - --silent
    if ($LASTEXITCODE -ne 0) { throw "デプロイ可能なタグ(v*)の制限を設定できませんでした。" }
}

# 秘密は標準入力で渡し、コマンドライン引数やログに出さない。
[Convert]::ToBase64String($pfxBytes) | gh secret set SELF_SIGN_PFX_BASE64 --repo $Repository --env $Environment
if ($LASTEXITCODE -ne 0) { throw "SELF_SIGN_PFX_BASE64 を登録できませんでした。" }
$password | gh secret set SELF_SIGN_PFX_PASSWORD --repo $Repository --env $Environment
if ($LASTEXITCODE -ne 0) { throw "SELF_SIGN_PFX_PASSWORD を登録できませんでした。" }
gh variable set SELF_SIGN_THUMBPRINT --repo $Repository --env $Environment --body $thumbprint
if ($LASTEXITCODE -ne 0) { throw "SELF_SIGN_THUMBPRINT を登録できませんでした。" }

Write-Host "Environment「$Environment」へ登録し、必須レビュアー(あなた)とデプロイ可能なタグ(v*)の制限を設定しました。"
Write-Host "再実行すると証明書が作り直され、thumbprintが変わります(以前の署名とは別の証明書になります)。"
