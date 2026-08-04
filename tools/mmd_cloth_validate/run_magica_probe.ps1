# 跑遊戲內的布料探針,產出 magica_<scenario>.json(compare.py 的 Unity 那一半)。
#
# ⚠️ 為什麼是「跑 player」而不是「跑 PlayMode 測試」:
#   Magica Cloth 2 在 Unity Test Framework(-batchmode -runTests)底下不會 step —— 連原廠的
#   BoneCloth 都不動(MmdPhysicsProbe 內建的 canary 就是為此)。那條路徑錄出來的每條鏈都是
#   完美剛性(形變 0.000000),compare.py 的 DATA VALIDITY 檢查會直接把整份報告標成無效。
#   布料在「真的遊戲」裡跑得好好的,所以就在那裡量:build 一個 player,用 -mmdprobe 啟動,
#   它跑完 4 個情境會自己關掉,recording 落在 exe 旁邊的 mmd_cloth_validate\。
#   (舊版這支腳本跑的就是那條死路,留下來的 magica_*.json 全是假資料。)
#
# 用法:
#   ./tools/mmd_cloth_validate/run_magica_probe.ps1              # build + 跑 + 收檔 + 算指標
#   ./tools/mmd_cloth_validate/run_magica_probe.ps1 -SkipBuild   # 直接用現成的 Build\Windows
# 之後 `python compare.py` 比對 pybullet 地面真值。
[CmdletBinding()]
param(
    [switch]$SkipBuild,          # 不重 build,直接跑現成的 exe
    [string]$BuildOut,           # 預設 <repo>\Build\Windows
    [string]$Unity,              # 傳給 build_windows.ps1
    [int]$TimeoutSec = 300
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here "..\..")).Path
if (-not $BuildOut) { $BuildOut = Join-Path $repo 'Build\Windows' }
$exe = Join-Path $BuildOut 'dance.exe'

if (-not $SkipBuild) {
    # DATA 已經在旁邊就不用再搬一次(幾 GB);exe 每次都重編,因為要測的就是新程式碼。
    $args = @{ BuildOut = $BuildOut; NoRename = $true }
    if ($Unity) { $args.Unity = $Unity }
    if (Test-Path (Join-Path $BuildOut 'DATA\MODEL')) { $args.SkipData = $true }
    & (Join-Path $repo 'tools\build_windows.ps1') @args
    if ($LASTEXITCODE -ne 0) { Write-Error "build failed (exit $LASTEXITCODE)"; exit 1 }
}
if (-not (Test-Path $exe)) { Write-Error "找不到 $exe —— 先 build(去掉 -SkipBuild)"; exit 1 }
if (-not (Test-Path (Join-Path $BuildOut 'DATA\MODEL'))) {
    Write-Error "$BuildOut\DATA\MODEL 不在 —— 探針需要模型,重跑一次不要 -SkipBuild"; exit 1
}

# 舊的 recording 先清掉,免得 build/跑失敗時拿到上一輪的檔還以為成功了。
$outDir = Join-Path $BuildOut 'mmd_cloth_validate'
if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }

Write-Host "[probe] $exe -mmdprobe (rest/turn/walk/spin,約 40 秒)" -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -ArgumentList '-mmdprobe' -PassThru
if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    try { $p.Kill() } catch { }
    Write-Error "探針超過 $TimeoutSec 秒還沒結束(遊戲卡住?看 $BuildOut\DATA\log.txt 的 [mmdprobe] 行)"; exit 1
}

$files = @(Get-ChildItem (Join-Path $outDir 'magica_*.json') -ErrorAction SilentlyContinue)
if ($files.Count -lt 4) {
    Write-Error "只錄到 $($files.Count)/4 個情境 —— 看 $BuildOut\DATA\log.txt 的 [mmdprobe] 行(CANARY FROZEN = MC2 沒在 step)"; exit 1
}
Copy-Item $files.FullName $here -Force
foreach ($f in $files) { Write-Host ("  {0}  {1:N0} bytes" -f $f.Name, $f.Length) }

python (Join-Path $here 'compute_metrics_magica.py') magica
Write-Host "接著跑:python $here\compare.py" -ForegroundColor Cyan
