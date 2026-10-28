# 配布物の中から、署名対象にする自社製のバイナリだけを返す。
# 第三者のruntimeは配布元の署名・固定hashで扱うため含めない。署名・検証・署名依頼で同じ規則を使う。

# { 責務: [Get-OwnBinaries: 配布フォルダ内の YourSinger.exe / YourSinger.ML.exe / YourSinger.*.dll を返す] }
function Get-OwnBinaries([string]$DistributionDirectory) {
    @(Get-ChildItem $DistributionDirectory -Recurse -File |
        Where-Object { $_.Name -in @("YourSinger.exe", "YourSinger.ML.exe") -or $_.Name -like "YourSinger.*.dll" })
}
