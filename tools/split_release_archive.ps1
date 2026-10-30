<#
  最終成果物のZIPを、GitHub Releaseに添付できる大きさ(既定1900MiB、上限は2GiB)のファイルに分割します(#72)。
  使い方: pwsh tools/split_release_archive.ps1 [-OutputDirectory artifacts/release] [-ArchiveName YourSinger-windows-x64.zip] [-PartSizeBytes 1992294400]
  分割ファイルは <ZIP名>.001, .002, ... で、ZIPと同じフォルダに作ります(元のZIPは残します)。
  各分割ファイルのSHA-256を、既存のSHA256SUMS(ZIP全体の行)の末尾へ追記します。結合したZIPは、ZIP全体の行で確認できます。
  結合: 番号順に連結します(README「配布物の検証」を参照)。
#>
param(
    [string]$OutputDirectory = "artifacts/release",
    [string]$ArchiveName = "YourSinger-windows-x64.zip",
    # GitHub Releaseの1ファイルの上限は2GiB(2147483648バイト)。余裕を持たせる。
    [ValidateRange(1MB, 2147483647)][long]$PartSizeBytes = 1900MB
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "BuildPathSafety.ps1")
$output = Resolve-ArtifactBuildPath $repo $OutputDirectory
$archivePath = Join-Path $output $ArchiveName
$sumsPath = Join-Path $output "SHA256SUMS"
foreach ($requiredPath in @($archivePath, $sumsPath)) {
    if (-not (Test-Path $requiredPath -PathType Leaf)) { throw "先にpackage_distribution_archive.ps1を実行してください: $requiredPath" }
}
# -Filter の "名前.*" は拡張子なしの名前(ZIP自体)にも一致するため、番号付きの名前だけを -like で判定する。
if (Get-ChildItem $output | Where-Object { $_.Name -like "$ArchiveName.[0-9][0-9][0-9]" }) { throw "分割ファイルが既にあります。作り直す場合は先にpackage_distribution_archive.ps1を実行してください。" }

# { 責務: [Write-ArchiveParts: ZIPをバッファ単位で読み、指定サイズごとに分割ファイルへ書く。各ファイルのSHA-256を返す] }
function Write-ArchiveParts {
    $buffer = New-Object byte[] (8MB)
    $lines = @()
    $source = [IO.File]::OpenRead($archivePath)
    try {
        $index = 0
        while ($source.Position -lt $source.Length) {
            $index++
            $partName = "{0}.{1:D3}" -f $ArchiveName, $index
            $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            $part = [IO.File]::Create((Join-Path $output $partName))
            try {
                $remaining = [Math]::Min($PartSizeBytes, $source.Length - $source.Position)
                while ($remaining -gt 0) {
                    $read = $source.Read($buffer, 0, [int][Math]::Min($buffer.Length, $remaining))
                    if ($read -le 0) { throw "ZIPの読み込みが途中で終了しました。" }
                    $part.Write($buffer, 0, $read)
                    $hash.AppendData($buffer, 0, $read)
                    $remaining -= $read
                }
            } finally { $part.Dispose() }
            $lines += "{0} *{1}" -f ([Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()), $partName
        }
    } finally { $source.Dispose() }
    return $lines
}

$partLines = Write-ArchiveParts
[IO.File]::AppendAllText($sumsPath, (($partLines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host "Split into $($partLines.Count) parts (max $PartSizeBytes bytes each): $output"
