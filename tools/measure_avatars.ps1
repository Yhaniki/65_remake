#Requires -Version 5.1
<#
.SYNOPSIS
  量「同時有 N 隻角色」的幀時間 —— 打歌畫面(舞者)與房間(座位 + 旁觀)各一組。

.DESCRIPTION
  多人同場最大的未知就是這個:六隻 CPU 蒙皮的舞者、以及房間最壞情況的 6 座位 + 10 旁觀 = 16 隻。
  計畫把量測排在所有優化之前 —— 先知道數字,再決定要不要動 GPU skinning / LOD,
  而不是先寫一套優化再回頭發現不需要。

  🔴 為什麼一定要跑**打包版**而不是編輯器:編輯器有 profiler/domain 額外開銷,而且 Editor 的
     渲染路徑與 player 不同。數字要能拿來做決定,就得量真的那個 exe。

  兩組都走**離線**(不需要 server、不需要湊真人):
    • 打歌:SDO_DANCERS=n 生 n 隻舞者(共用骨架/動作/編舞,站官方隊形座標)。
      進遊戲靠 SDO_AUTOSTART(離線單人房不需要別人準備)。
    • 房間:SDO_ROOMAVATARS=n 把房間補到 n 隻真 avatar(男女交錯,兩套部件都會載到)。

  結果從 log.txt 撈 "[perf]" 行(ScreenGameplay/RoomScreen 每 2 秒印一次平均與最差幀)。
  ⚠️ SDO_VERBOSE=1 是必要的 —— 打包版預設把 Debug.Log 全丟掉(見 SdoLog.OnLog)。

.EXAMPLE
  pwsh -File tools\measure_avatars.ps1
  pwsh -File tools\measure_avatars.ps1 -Seconds 30 -Cases 1,4,6
#>
[CmdletBinding()]
param(
    [string]$Exe,
    # 打歌畫面要量的舞者數(含本機)
    [int[]]$Cases = @(1, 6),
    # 房間要量的角色數(6 座位 + 10 旁觀 = 16 是官方上限)
    [int[]]$RoomCases = @(1, 6, 16),
    [int]$Seconds = 24,
    [int]$BootSec = 45,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo 'Build\Windows\dance.exe' }
if (-not $Out) { $Out = Join-Path $repo 'avatar_perf.md' }
if (-not (Test-Path $Exe)) { throw "找不到 $Exe(先跑 tools\build_windows.ps1)" }
$logSrc = Join-Path (Split-Path -Parent $Exe) 'log.txt'

function Kill-Dance {
    # 殘留的 dance.exe 會改寫 config.ini 並抹掉 [Net](踩過兩次),而且會搶 GPU 讓量測失真。
    Get-Process dance -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "[perf] 先關掉殘留的 dance.exe pid $($_.Id)"
        Stop-Process -Id $_.Id -Force
    }
    Start-Sleep -Milliseconds 400
}

# 一次量測:設好環境變數 → 開遊戲 → 等 → 關掉 → 從 log 撈 [perf] 行
function Measure-One([hashtable]$env2, [string]$label) {
    Kill-Dance
    foreach ($k in @('SDO_DANCERS','SDO_ROOMAVATARS','SDO_ROOM','SDO_AUTOSTART','SDO_AUTOPLAY','SDO_VERBOSE')) {
        Set-Item -Path "env:$k" -Value ''
    }
    $env:SDO_VERBOSE = '1'
    foreach ($k in $env2.Keys) { Set-Item -Path "env:$k" -Value $env2[$k] }

    Write-Host "[perf] === $label ==="
    $p = Start-Process -FilePath $Exe -PassThru
    $null = $p.Handle
    Start-Sleep -Seconds ($BootSec + $Seconds)
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    Start-Sleep -Milliseconds 600

    if (-not (Test-Path $logSrc)) { Write-Warning "[perf] 沒有 log.txt"; return @() }
    $lines = Select-String -Path $logSrc -Pattern '\[perf\]' | ForEach-Object { $_.Line }
    if (-not $lines) { Write-Warning "[perf] $label 沒有量到任何 [perf] 行(可能還沒進到那個畫面)" }
    foreach ($l in $lines) { Write-Host "    $l" }
    return $lines
}

$report = New-Object System.Collections.Generic.List[string]
$report.Add('# 角色數 vs 幀時間(打包版實機量測)')
$report.Add('')
$report.Add("量測時間:每組 $Seconds 秒(開機/載入的 $BootSec 秒不算)。exe:``$Exe``")
$report.Add('')
$report.Add('數字由 `FrameStats` 每 2 秒印一行:平均幀時間、最差幀。**最差幀比平均重要** ——')
$report.Add('節奏遊戲裡單一 60ms 的尖峰就是一次看得見的頓,而平均會把它完全藏起來。')
$report.Add('')

foreach ($n in $RoomCases) {
    # 🔴 一定要帶 SDO_ROOM=1:沒有它 client 會停在選男女畫面,永遠進不了房間 → 一行 [perf] 都量不到
    #    (第一次跑就是這樣,五組全部 WARNING)。
    $lines = Measure-One @{ SDO_ROOMAVATARS = "$n"; SDO_ROOM = '1' } "房間 $n 隻角色"
    $report.Add("## 房間 $n 隻")
    $report.Add('```')
    foreach ($l in $lines) { $report.Add($l) }
    $report.Add('```')
    $report.Add('')
}

foreach ($n in $Cases) {
    # 離線單人房:SDO_AUTOSTART 直接開場(不需要別人準備);SDO_AUTOPLAY 代打讓分數/特效也在跑,
    # 才不會量到「什麼都沒發生」的空場成本。
    $lines = Measure-One @{ SDO_DANCERS = "$n"; SDO_ROOM = '1'; SDO_AUTOSTART = '1'; SDO_AUTOPLAY = '1' } "打歌 $n 隻舞者"
    $report.Add("## 打歌畫面 $n 隻舞者")
    $report.Add('```')
    foreach ($l in $lines) { $report.Add($l) }
    $report.Add('```')
    $report.Add('')
}

Kill-Dance
foreach ($k in @('SDO_DANCERS','SDO_ROOMAVATARS','SDO_ROOM','SDO_AUTOSTART','SDO_AUTOPLAY','SDO_VERBOSE')) {
    Set-Item -Path "env:$k" -Value ''
}
[System.IO.File]::WriteAllLines($Out, $report)
Write-Host "[perf] 報告 → $Out"
