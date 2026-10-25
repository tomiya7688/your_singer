<#
  配布フォルダを利用者へ渡す最終成果物(ZIPとSHA256SUMS)にまとめます。
  使い方: pwsh tools/package_distribution_archive.ps1 [-DistributionDirectory artifacts/windows-distribution]
                                                      [-OutputDirectory artifacts/release]
  署名・attestation は、ここで作ったZIPとSHA256SUMS自体に対して行います。
#>
param(
    [string]$DistributionDirectory = "artifacts/windows-distribution",
    [string]$OutputDirectory = "artifacts/release",
    [string]$ArchiveName = "YourSinger-windows-x64.zip"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "BuildPathSafety.ps1")
$distribution = Resolve-ArtifactBuildPath $repo $DistributionDirectory
$output = Resolve-ArtifactBuildPath $repo $OutputDirectory
if (-not (Test-Path (Join-Path $distribution "distribution-manifest.json") -PathType Leaf)) {
    throw "配布フォルダにdistribution-manifest.jsonがありません。先にbuild_windows_distribution.ps1を実行してください。"
}

Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $output | Out-Null
$archivePath = Join-Path $output $ArchiveName

# 2GBを超える配布物でも作れるよう、Compress-Archiveではなく.NETのZIP64対応APIを使う。
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($distribution, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)

# sha256sum互換の形式にし、利用者が標準ツールで照合できるようにする。
$archiveHash = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$sumsPath = Join-Path $output "SHA256SUMS"
[IO.File]::WriteAllText($sumsPath, "$archiveHash *$ArchiveName`n", [Text.UTF8Encoding]::new($false))

Write-Host "Archive: $archivePath"
Write-Host "SHA256:  $archiveHash"
