# {
#   責務: [Resolve-ArtifactBuildPath: 再帰削除する構築先をリポジトリのartifacts配下へ限定する]
#   処理: [1: 絶対パスを確認する 2: artifacts自体と外部参照のjunctionを拒否する]
#   引数: [Repository: リポジトリ, RelativePath: 構築先]
#   戻り値: [ValidatedPath: 検証した構築先の絶対パス]
#   エラー: [範囲外または再解析ポイントを含む場合に失敗する]
# }
function Resolve-ArtifactBuildPath {
    param([string]$Repository, [string]$RelativePath)
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $Repository 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $validatedPath = [IO.Path]::GetFullPath($RelativePath, $Repository).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $validatedPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '構築先はリポジトリのartifacts内の専用フォルダにしてください。'
    }
    # 既存のjunctionを経由して外へ削除対象が広がる経路を拒否する。
    $ancestorPath = $validatedPath
    while ($ancestorPath.Length -ge $artifactRoot.Length) {
        if (Test-Path -LiteralPath $ancestorPath) {
            $attributes = (Get-Item -LiteralPath $ancestorPath -Force).Attributes
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw '構築先にjunctionを含めることはできません。' }
        }
        $ancestorPath = Split-Path -Parent $ancestorPath
    }
    return $validatedPath
}

# {
#   責務: [Assert-IndependentBuildSource: 構築先の削除が入力bundleへ及ばないことを確認する]
#   処理: [1: 入力と各出力先の親子関係・同一性を拒否する]
#   引数: [SourceDirectory: 入力, OutputDirectories: 削除し得る構築先]
#   エラー: [入力と構築先が重なる場合に失敗する]
# }
function Assert-IndependentBuildSource {
    param([string]$SourceDirectory, [string[]]$OutputDirectories)
    $sourcePath = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    foreach ($directory in $OutputDirectories) {
        $outputPath = [IO.Path]::GetFullPath($directory).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ($sourcePath.Equals($outputPath, [StringComparison]::OrdinalIgnoreCase) -or
            $sourcePath.StartsWith($outputPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            $outputPath.StartsWith($sourcePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw '構築先と入力bundleは別の専用フォルダにしてください。'
        }
    }
}
