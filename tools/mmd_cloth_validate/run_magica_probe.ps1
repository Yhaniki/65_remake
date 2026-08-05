# 跑遊戲內的布料探針,產出 magica_<scenario>.json(compare.py 的 Unity 那一半)。
#
# ⚠️ 為什麼是「跑 player」而不是「跑 PlayMode 測試」:
#   Magica Cloth 2 在 Unity Test Framework(-batchmode -runTests)底下不會 step —— 連原廠的
#   BoneCloth 都不動(MmdPhysicsProbe 內建的 canary 就是為此)。那條路徑錄出來的每條鏈都是
#   完美剛性(形變 0.000000),compare.py 的 DATA VALIDITY 檢查會直接把整份報告標成無效。
#   布料在「真的遊戲」裡跑得好好的,所以就在那裡量:build 一個 player,用 -mmdprobe 啟動,
#   它跑完 5 個情境會自己關掉；recording 直接寫入這個 worktree 的隔離輸出資料夾。
#   (舊版這支腳本跑的就是那條死路,留下來的 magica_*.json 全是假資料。)
#
# 用法:
#   ./tools/mmd_cloth_validate/run_magica_probe.ps1              # build + 跑 + 收檔 + 算指標
#   ./tools/mmd_cloth_validate/run_magica_probe.ps1 -SkipBuild   # 直接用現成的 Build\Windows
#   ./tools/mmd_cloth_validate/run_magica_probe.ps1 -SkipBuild -ModelPath H:\models\miku.pmx -ModelId miku
# ModelId 只影響輸出資料夾與畫面 label；模型身分由 model.json 的 PMX SHA 驗證，不以 ModelId 認證。
# 之後 `python compare.py` 比對 pybullet 地面真值。
[CmdletBinding()]
param(
    [switch]$SkipBuild,          # 不重 build,直接跑現成的 exe
    [string]$BuildOut,           # 預設 <repo>\Build\Windows
    [string]$Unity,              # 傳給 build_windows.ps1
    [int]$TimeoutSec = 300,
    [string]$ModelPath,          # 明確指定任意 PMX；不指定時沿用遊戲模型清單
    [string]$ModelId,            # 僅作輸出 label；不是 manifest identity，也不驗證 corpus fixture
    [string]$OutputDir           # 明確指定時必須位於 <repo>\test-output
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here "..\..")).Path
$scenarioNames = @('rest', 'turn', 'walk', 'spin', 'dance')
$scenarioFiles = @($scenarioNames | ForEach-Object { "magica_$_.json" })

function Resolve-FullPath([string]$Value, [string]$BasePath) {
    $full = if ([IO.Path]::IsPathRooted($Value)) {
        [IO.Path]::GetFullPath($Value)
    } else {
        [IO.Path]::GetFullPath((Join-Path $BasePath $Value))
    }
    $root = [IO.Path]::GetPathRoot($full)
    if ($full -ne $root) {
        $full = $full.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }
    return $full
}

function Test-PathInside([string]$RootPath, [string]$TargetPath) {
    if ($TargetPath.Equals($RootPath, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $prefix = $RootPath + [IO.Path]::DirectorySeparatorChar
    return $TargetPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoExistingReparsePoint([string]$RootPath, [string]$TargetPath) {
    $paths = @($RootPath)
    if (-not $TargetPath.Equals($RootPath, [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $TargetPath.Substring($RootPath.Length).TrimStart(
            [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $current = $RootPath
        foreach ($part in @($relative -split '[\\/]' | Where-Object { $_ })) {
            $current = Join-Path $current $part
            $paths += $current
        }
    }
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "OutputDir 路徑含 reparse point，拒絕寫入或刪除: $path"
        }
        if (-not $item.PSIsContainer) {
            throw "OutputDir 路徑元件不是資料夾: $path"
        }
    }
}

function Quote-ProcessArgument([string]$Value) {
    if ($Value.Contains('"')) { throw "process argument contains an invalid quote: $Value" }
    return '"' + $Value + '"'
}

if (-not $BuildOut) { $BuildOut = Join-Path $repo 'Build\Windows' }
$BuildOut = Resolve-FullPath $BuildOut $repo
$exe = Join-Path $BuildOut 'dance.exe'

if ($ModelId -and $ModelId -notmatch '^[a-z0-9][a-z0-9_-]*$') {
    Write-Error "ModelId 必須符合 [a-z0-9][a-z0-9_-]*: $ModelId"; exit 1
}
if ($ModelPath) {
    $ModelPath = Resolve-FullPath $ModelPath $repo
    if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) {
        Write-Error "找不到 PMX: $ModelPath"; exit 1
    }
    if ([IO.Path]::GetExtension($ModelPath) -ine '.pmx') {
        Write-Error "ModelPath 必須是 .pmx: $ModelPath"; exit 1
    }
}

$outputWasExplicit = $PSBoundParameters.ContainsKey('OutputDir')
if ($outputWasExplicit -and [string]::IsNullOrWhiteSpace($OutputDir)) {
    Write-Error "明確指定的 OutputDir 不可為空"; exit 1
}
if (-not $OutputDir) {
    $OutputDir = if ($ModelId) { Join-Path $repo ("test-output\mmd-cloth-probe\" + $ModelId) } else { $here }
}
$OutputDir = Resolve-FullPath $OutputDir $repo
$repoRoot = Resolve-FullPath $repo $repo
$testOutputRoot = Resolve-FullPath (Join-Path $repoRoot 'test-output') $repoRoot
if ($OutputDir.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Error "OutputDir 不可為 worktree 根目錄: $OutputDir"; exit 1
}
$insideTestOutput = Test-PathInside $testOutputRoot $OutputDir
if ($outputWasExplicit -and -not $insideTestOutput) {
    Write-Error "明確指定的 OutputDir 必須位於 $testOutputRoot 內: $OutputDir"; exit 1
}
if (-not $outputWasExplicit -and $ModelId -and -not $insideTestOutput) {
    Write-Error "ModelId 隔離輸出必須位於 $testOutputRoot 內: $OutputDir"; exit 1
}
if ($insideTestOutput) {
    # Check every existing component before either creating directories or deleting contract files. A lexical prefix
    # check alone is insufficient because a junction below test-output could redirect those exact deletes elsewhere.
    Assert-NoExistingReparsePoint $testOutputRoot $OutputDir
}
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null

if (-not $SkipBuild) {
    # DATA 已經在旁邊就不用再搬一次(幾 GB);exe 每次都重編,因為要測的就是新程式碼。
    $args = @{ BuildOut = $BuildOut; NoRename = $true }
    if ($Unity) { $args.Unity = $Unity }
    if (Test-Path (Join-Path $BuildOut 'DATA\MODEL')) { $args.SkipData = $true }
    & (Join-Path $repo 'tools\build_windows.ps1') @args
    if ($LASTEXITCODE -ne 0) { Write-Error "build failed (exit $LASTEXITCODE)"; exit 1 }
}
if (-not (Test-Path $exe)) { Write-Error "找不到 $exe —— 先 build(去掉 -SkipBuild)"; exit 1 }
if (-not $ModelPath -and -not (Test-Path (Join-Path $BuildOut 'DATA\MODEL'))) {
    Write-Error "$BuildOut\DATA\MODEL 不在 —— 探針需要模型,重跑一次不要 -SkipBuild"; exit 1
}

# 只清掉這份固定輸出契約中的六個檔案。OutputDir 已先驗證位於 worktree 內；不刪整個資料夾。
foreach ($name in @($scenarioFiles + 'model.json')) {
    $target = Join-Path $OutputDir $name
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
}

$probeArgs = @('-mmdprobe')
if ($ModelPath) { $probeArgs += @('-mmdprobe-pmx', (Quote-ProcessArgument $ModelPath)) }
$probeArgs += @('-mmdprobe-out', (Quote-ProcessArgument $OutputDir))
$argumentLine = $probeArgs -join ' '
$label = if ($ModelId) { $ModelId } elseif ($ModelPath) { [IO.Path]::GetFileNameWithoutExtension($ModelPath) } else { 'legacy-selected-model' }
Write-Host "[probe:$label] $exe $argumentLine (5 scenarios)" -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -ArgumentList $argumentLine -WindowStyle Hidden -PassThru
$null = $p.Handle   # Windows PowerShell 5: cache the native handle now or ExitCode can later read as -1.
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    try { $p.Kill() } catch { }
    Write-Error "探針超過 $TimeoutSec 秒還沒結束(遊戲卡住?看 $BuildOut\log.txt 的 [mmdprobe] 行)"; exit 1
}
$playerExitCode = $p.ExitCode
if ($playerExitCode -ne 0 -and $playerExitCode -ne -1) {
    Write-Error "探針失敗(exit $playerExitCode) —— 看 $BuildOut\log.txt 的 [mmdprobe] 行"; exit 1
}
if ($playerExitCode -eq -1) {
    Write-Warning "Unity player 回報 exit -1；延後到完整檔案、模型 SHA 與 cloth liveness 驗證後再判定"
}

$files = @(Get-ChildItem -LiteralPath $OutputDir -Filter 'magica_*.json' -File -ErrorAction SilentlyContinue)
$actualNames = @($files | ForEach-Object { $_.Name })
$missing = @($scenarioFiles | Where-Object { $_ -notin $actualNames })
# magica_metrics.json 是 default legacy 流程的衍生檔，不是 recording；其餘未知 magica_*.json 則拒絕，
# 避免呼叫端把上一個版本多出來的情境誤認成這一輪結果。
$unexpected = @($actualNames | Where-Object { $_ -notin $scenarioFiles -and $_ -ne 'magica_metrics.json' })
$modelJson = Join-Path $OutputDir 'model.json'
if ($missing.Count -ne 0 -or $unexpected.Count -ne 0 -or -not (Test-Path -LiteralPath $modelJson -PathType Leaf)) {
    Write-Error "輸出契約不完整：missing=[$($missing -join ', ')] unexpected=[$($unexpected -join ', ')] model.json=$(Test-Path -LiteralPath $modelJson)"; exit 1
}
foreach ($name in @($scenarioFiles + 'model.json')) {
    $f = Get-Item -LiteralPath (Join-Path $OutputDir $name)
    Write-Host ("  {0}  {1:N0} bytes" -f $f.Name, $f.Length)
}

$validator = Join-Path $here 'validate_probe_run.py'
$validatorArgs = @($validator, $OutputDir)
if ($ModelPath) {
    $expectedSha = (Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash
    $validatorArgs += @('--expected-sha', $expectedSha)
}
& python @validatorArgs
if ($LASTEXITCODE -ne 0) { Write-Error "probe validation failed (exit $LASTEXITCODE)"; exit $LASTEXITCODE }
if ($playerExitCode -eq -1) {
    Write-Host "accepted Unity exit -1: strict probe validation passed" -ForegroundColor Yellow
}

if ($OutputDir -eq [IO.Path]::GetFullPath($here)) {
    python (Join-Path $here 'compute_metrics_magica.py') magica
    if ($LASTEXITCODE -ne 0) { Write-Error "metric computation failed (exit $LASTEXITCODE)"; exit $LASTEXITCODE }
    Write-Host "接著跑: python $here\compare.py" -ForegroundColor Cyan
} else {
    Write-Host "validated output: $OutputDir" -ForegroundColor Cyan
}
