<#
  配布フォルダのdistribution-manifest.jsonにある全ファイルのサイズとSHA-256を、現在の内容で書き直します。
  署名でバイナリが変わった後にも実行し、manifestと配布物を一致させます。
  使い方: pwsh tools/update_distribution_manifest.ps1 [-DistributionDirectory artifacts/windows-distribution]
#>
param([string]$DistributionDirectory = "artifacts/windows-distribution")

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "BuildPathSafety.ps1")
$distribution = Resolve-ArtifactBuildPath $repo $DistributionDirectory
$manifestPath = Join-Path $distribution "distribution-manifest.json"
if (-not (Test-Path $manifestPath -PathType Leaf)) { throw "distribution-manifest.jsonがありません: $manifestPath" }

$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
# manifest自身は一覧に含めない(自分のhashは書き込み前に確定できないため)。
$files = @(Get-ChildItem $distribution -File -Recurse |
    Where-Object { $_.FullName -ne $manifestPath } |
    Sort-Object FullName |
    ForEach-Object {
        [PSCustomObject]@{
            path = [IO.Path]::GetRelativePath($distribution, $_.FullName).Replace("\", "/")
            size = $_.Length
            sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
$manifest | Add-Member -NotePropertyName files -NotePropertyValue $files -Force
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $manifestPath
Write-Host "Manifest updated: $($files.Count) files"
