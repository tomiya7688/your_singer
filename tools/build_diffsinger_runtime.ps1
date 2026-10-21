param(
    [Parameter(Mandatory = $true)][string]$PythonRuntimeDirectory,
    [string]$DiffSingerSourceDirectory = "artifacts/diffsinger-source/DiffSinger-323a5693245b827caba0ab1a7371fe5ca885b061",
    [string]$OutputDirectory = "artifacts/diffsinger-runtime"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "BuildPathSafety.ps1")
$output = Resolve-ArtifactBuildPath $repo $OutputDirectory
$sourceRuntime = [IO.Path]::GetFullPath($PythonRuntimeDirectory, $repo)
$diffSingerSource = [IO.Path]::GetFullPath($DiffSingerSourceDirectory, $repo)
Assert-IndependentBuildSource $sourceRuntime @($output)
Assert-IndependentBuildSource $diffSingerSource @($output)
if (Test-Path -LiteralPath $output) { throw "DiffSinger runtimeの構築先は未作成のフォルダにしてください。" }
$staging = Resolve-ArtifactBuildPath $repo ('artifacts/.diffsinger-runtime-' + [guid]::NewGuid().ToString('N'))
Assert-IndependentBuildSource $sourceRuntime @($staging)
Assert-IndependentBuildSource $diffSingerSource @($staging)
if (Test-Path -LiteralPath (Join-Path $sourceRuntime 'pyvenv.cfg')) { throw "venvではなく完全なWindows Python runtimeを指定してください。" }
$sourcePython = Join-Path $sourceRuntime 'python.exe'
if (-not (Test-Path -LiteralPath $sourcePython -PathType Leaf)) { throw "入力Pythonがありません。" }
$env:TEMP = Resolve-ArtifactBuildPath $repo 'artifacts/diffsinger-runtime-temp'
$env:TMP = $env:TEMP
$env:PIP_CACHE_DIR = Resolve-ArtifactBuildPath $repo 'artifacts/pip-cache'
$env:PYTHONPYCACHEPREFIX = Resolve-ArtifactBuildPath $repo 'artifacts/python-bytecode-cache'
$env:PYTHONUTF8 = '1'
Remove-Item Env:PYTHONHOME -ErrorAction SilentlyContinue
Remove-Item Env:PYTHONPATH -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

$version = & $sourcePython -c "import platform; print(platform.python_version())"
if ($LASTEXITCODE -ne 0 -or $version.Trim() -ne '3.12.10') { throw "Python 3.12.10が必要です。" }
# DiffSinger用へ複製するPythonは標準構成だけにし、他のMLフレームワークを混ぜない。
& $sourcePython -c "import importlib.metadata as m; extra={d.metadata['Name'].lower() for d in m.distributions()}-{'pip','setuptools','wheel'}; assert not extra, extra"
if ($LASTEXITCODE -ne 0) { throw "入力Pythonは追加パッケージのない完全なruntimeにしてください。" }
$sourceProbe = Join-Path $PSScriptRoot 'prepare_diffsinger_source.py'
& $sourcePython $sourceProbe --archive (Join-Path $repo 'artifacts/diffsinger-source/v2.5.1.zip') --output $diffSingerSource --verify-only
if ($LASTEXITCODE -ne 0) { throw "固定DiffSingerソースを検証できません。" }

try {
New-Item -ItemType Directory -Path $staging | Out-Null
# Python本体と固定ソースを専用の一時領域へ複製して検証する。
$runtimeDirectory = Join-Path $staging 'runtime'
& $sourcePython -c "import shutil,sys; shutil.copytree(sys.argv[1],sys.argv[2],ignore=shutil.ignore_patterns('__pycache__','*.pyc'))" $sourceRuntime $runtimeDirectory
if ($LASTEXITCODE -ne 0) { throw "DiffSinger runtime用Pythonの複製に失敗しました。" }
$python = Join-Path $runtimeDirectory 'python.exe'
$bundleSource = Join-Path $staging 'source'
& $sourcePython -c "import shutil,sys; shutil.copytree(sys.argv[1],sys.argv[2],ignore=shutil.ignore_patterns('__pycache__','*.pyc','.git'))" $diffSingerSource $bundleSource
if ($LASTEXITCODE -ne 0) { throw "固定DiffSingerソースをruntime bundleへ複製できません。" }
$resourceLock = Join-Path $repo 'src/process/YourSinger.Process/processing/ml/worker/resources/diffsinger-source.lock.json'
Copy-Item -LiteralPath $resourceLock -Destination (Join-Path $staging 'diffsinger-source.lock.json')
& $python -m pip install --no-compile --requirement (Join-Path $PSScriptRoot 'diffsinger-training-requirements.lock.txt') --extra-index-url 'https://download.pytorch.org/whl/cu121' --report (Join-Path $staging 'dependencies-install-report.json')
if ($LASTEXITCODE -ne 0) { throw "固定DiffSinger Trainer依存を導入できません。" }
& $python -m pip check
if ($LASTEXITCODE -ne 0) { throw "DiffSinger Trainer runtimeの依存が競合しています。" }

# Trainer入口が同梱されたPythonと固定ソースの組合せで読み込めるかを確認する。
& $python (Join-Path $PSScriptRoot 'probe_diffsinger_runtime.py') --root $runtimeDirectory --source $bundleSource --output (Join-Path $staging 'runtime-probe.json') --lock (Join-Path $staging 'diffsinger-source.lock.json')
if ($LASTEXITCODE -ne 0) { throw "DiffSinger Trainer runtimeの読込みを確認できません。" }
& $python -m pip freeze --all | Set-Content -Encoding utf8 (Join-Path $staging 'installed-requirements.txt')
if ($LASTEXITCODE -ne 0) { throw "導入した依存一覧を保存できません。" }

@{ schema_version = 1; source_version = '2.5.1'; source_commit = '323a5693245b827caba0ab1a7371fe5ca885b061';
    runtime = 'runtime'; source = 'source'; source_lock = 'diffsinger-source.lock.json';
    pretrained_vocoder_included = $false; training_run = $false } |
    ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $staging 'bundle-manifest.json')
# manifestを作成した後に検証済みbundleを配置する。
if (Test-Path -LiteralPath $output) { throw "検証中に同名のbundleが作成されました。" }
Move-Item -LiteralPath $staging -Destination $output
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
Write-Host "DiffSinger acoustic Trainer bundle: $output"
