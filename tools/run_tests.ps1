<#
.SYNOPSIS
  跑 Unity 的 EditMode / PlayMode 測試(無頭),即時串流 log,最後解析 results.xml 印摘要。

.DESCRIPTION
  照 tools\build_windows.ps1 的同一套模式:Start-Process -PassThru + 快取 Handle + 共享
  FileShare.ReadWrite 讀 log(Unity 鎖著檔案，plain `Get-Content -Wait` 會失效)+ WaitForExit。

  ⚠️ 三個踩過的坑，改這支腳本前先讀:

  1) **絕對不要加 -nographics。** 專案裡約 20 個測試會真的建 RenderTexture / 讀像素
     (RoomHeadPortraitFramingTests、各種 art / 渲染測試)。-nographics 下它們會全部假紅，
     看起來像程式壞了其實是跑測試的方式錯了。要無頭就用 -force-d3d11。

  2) **絕對不要加 -quit。** -runTests 跑完自己會退。加了 -quit 會讓 Unity 在測試還沒跑
     就關掉 —— 症狀是 exit code 0 但沒有 results.xml。

  3) **不要相信 Unity 的 exit code。** 它會在測試沒跑的情況下回 0(見上)，也會在測試全過
     的情況下回非 0。唯一的真相是 results.xml 的 failed 數。本腳本的 exit code 由 xml 決定。

  另外:Unity.exe 是 GUI-subsystem exe，直接 `& $unity` 呼叫會提早返回(等到的是 launcher，
  不是真正的 editor 進程)—— 所以一定要走 Start-Process -PassThru + WaitForExit。

  第一次在新 worktree 跑會很久(要 import 整個專案 + 編譯所有 assembly)，之後有 Library
  快取就快得多。Unity Editor 開著同一個專案時 Library 會被鎖 —— 先關掉 Editor 再跑。

.PARAMETER Unity
  Unity.exe 路徑。預設:Unity Hub 安裝目錄下最新的一版。

.PARAMETER ProjectPath
  要測的 Unity 專案。預設:<repo>\65\My project。

.PARAMETER Platform
  EditMode(預設，單元測試主力)或 PlayMode(截圖/整合測試)。

.PARAMETER Filter
  只跑符合的測試(Unity 的 -testFilter，吃 namespace/類別/方法名前綴)。省略 = 全跑。

.PARAMETER Results
  results.xml 輸出路徑。預設:<repo>\test_results-<platform>.xml
  (底線是刻意的 —— .gitignore 既有的 /test_results*.xml 才蓋得到)。

.PARAMETER LogFile
  Unity log 路徑。預設:<repo>\test-<platform>.log(每次跑會先清掉)。

.PARAMETER Quiet
  不即時串流 log(還是會寫檔)。CI 用。

.EXAMPLE
  ./tools/run_tests.ps1
.EXAMPLE
  ./tools/run_tests.ps1 -Filter Sdo.Tests.NetRoomRulesTests
.EXAMPLE
  ./tools/run_tests.ps1 -Platform PlayMode
#>
[CmdletBinding()]
param(
    [string]$Unity,
    [string]$ProjectPath,
    [ValidateSet('EditMode', 'PlayMode')]
    [string]$Platform = 'EditMode',
    [string]$Filter,
    [string]$Results,
    [string]$LogFile,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $ProjectPath) { $ProjectPath = Join-Path $Repo '65\My project' }
# 輸出一律進 <repo>	est-output\ —— 以前散在 repo 根，一輪測試就多兩個檔，久了根目錄全是垃圾。
$OutDir = Join-Path $Repo 'test-output'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
if (-not $Results)     { $Results     = Join-Path $OutDir "results-$($Platform.ToLower()).xml" }
if (-not $LogFile)     { $LogFile     = Join-Path $OutDir "$($Platform.ToLower()).log" }

# 找 Unity.exe:有給 -Unity 就用，否則挑 Hub 底下最新的一版(同 build_windows.ps1)。
if (-not $Unity) {
    $hub = 'C:\Program Files\Unity\Hub\Editor'
    if (Test-Path $hub) {
        $Unity = Get-ChildItem -Path $hub -Filter Unity.exe -Recurse -ErrorAction SilentlyContinue |
                 Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $Unity -or -not (Test-Path $Unity)) {
    throw "Unity.exe not found. Pass -Unity 'C:\path\to\Unity.exe'."
}
if (-not (Test-Path $ProjectPath)) { throw "ProjectPath not found: $ProjectPath" }

# Editor 開著同一個專案會鎖 Library,batchmode 會卡在等鎖 —— 先講清楚，不要讓人乾等。
$running = Get-Process Unity -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[test] WARNING: 有 Unity 進程在跑(PID $($running.Id -join ', '))。" -ForegroundColor Yellow
    Write-Host "[test]          如果它開的是同一個專案，Library 會被鎖住，這次會卡住或失敗。" -ForegroundColor Yellow
}

Write-Host "[test] unity    = $Unity"
Write-Host "[test] project  = $ProjectPath"
Write-Host "[test] platform = $Platform"
if ($Filter) { Write-Host "[test] filter   = $Filter" }
Write-Host "[test] results  = $Results"
Write-Host "[test] log      = $LogFile"
Write-Host ""

foreach ($f in $Results, $LogFile) { if (Test-Path $f) { Remove-Item $f -Force } }

# NOTE: Start-Process -ArgumentList (PS 5.1) 不會自動為含空白的元素加引號 ——
# "H:\65_remake\65\My project" 會被切成 "...\My" + "project"。路徑參數自己包引號。
# -force-d3d11 而不是 -nographics:見檔頭第 1 點。沒有 -quit:見檔頭第 2 點。
$argList = @(
    '-batchmode',
    '-runTests',
    '-force-d3d11',
    '-projectPath', "`"$ProjectPath`"",
    '-testPlatform', $Platform,
    '-testResults', "`"$Results`"",
    '-logFile', "`"$LogFile`""
)
if ($Filter) { $argList += @('-testFilter', "`"$Filter`"") }

$p = Start-Process -FilePath $Unity -PassThru -NoNewWindow -ArgumentList $argList
# 先快取 handle,否則進程結束後 $p.ExitCode 會變 null(PS 5.1 的怪癖)。
$null = $p.Handle

# 等 Unity 把 log 建出來,然後用共享讀取器邊跑邊跟。
while (-not (Test-Path $LogFile) -and -not $p.HasExited) { Start-Sleep -Milliseconds 200 }

if ((Test-Path $LogFile) -and -not $Quiet) {
    $fs = [System.IO.File]::Open($LogFile, 'Open', 'Read', 'ReadWrite')
    $sr = New-Object System.IO.StreamReader($fs)
    try {
        while (-not $p.HasExited) {
            $line = $sr.ReadLine()
            if ($null -ne $line) { Write-Host $line } else { Start-Sleep -Milliseconds 200 }
        }
        while ($null -ne ($line = $sr.ReadLine())) { Write-Host $line }   # 把剩下的讀完
    } finally {
        $sr.Dispose(); $fs.Dispose()
    }
}

$p.WaitForExit()
$unityCode = $p.ExitCode

Write-Host ""
Write-Host "=== Unity exit code: $unityCode (不可信,真相看下面的 results.xml) ==="

# ---- 唯一的真相:results.xml ----
if (-not (Test-Path $Results)) {
    Write-Host ""
    Write-Host "!!! 沒有產生 results.xml —— 測試根本沒跑起來。log 尾巴:" -ForegroundColor Red
    if (Test-Path $LogFile) { Get-Content $LogFile -Tail 40 -Encoding UTF8 }
    exit 1
}

[xml]$xml = Get-Content $Results -Encoding UTF8
$run = $xml.'test-run'
$failed = [int]$run.failed

Write-Host ""
Write-Host ("total={0}  passed={1}  failed={2}  skipped={3}  inconclusive={4}  ({5}s)" -f `
    $run.total, $run.passed, $run.failed, $run.skipped, $run.inconclusive, $run.duration)

if ($failed -gt 0) {
    Write-Host ""
    Write-Host "---- 失敗的測試 ----" -ForegroundColor Red
    foreach ($tc in $xml.SelectNodes("//test-case[@result='Failed']")) {
        Write-Host ("  {0}" -f $tc.fullname) -ForegroundColor Red
        $msg = $tc.failure.message
        if ($msg) {
            # 訊息可能很長(含 stack trace),只印前幾行讓人知道是什麼問題。
            ($msg -split "`n" | Select-Object -First 4) | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkRed }
        }
    }
}

$color = if ($failed -eq 0) { 'Green' } else { 'Red' }
Write-Host ""
Write-Host "=== $Platform : $(if ($failed -eq 0) { 'ALL GREEN' } else { "$failed FAILED" }) ===" -ForegroundColor $color
exit $failed
