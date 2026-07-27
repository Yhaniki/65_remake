<#
.SYNOPSIS
  Launch the built dance.exe, wait for it to settle, and grab its window as a .png.

.DESCRIPTION
  UI 品質只能實機驗證 —— 烘圖工具的輸出看起來對,疊上 TMP 之後字有沒有對準版位是另一回事。
  這個腳本把那個迴圈自動化:啟動 → 等它開完機 → 抓視窗 → 收工。

  怎麼抓:GDI 的 CopyFromScreen(把螢幕上那塊矩形複製下來),而不是叫遊戲自己截圖。
  試過走遊戲內建的 Screenshotter(綁 Print Screen)—— keybd_event 送 VK_SNAPSHOT 遊戲收不到,
  而且那條路還多依賴「視窗有沒有焦點」。直接抓螢幕少一層,而且要抓誰、抓多大都由這裡決定。
  ⚠️ CopyFromScreen 抓的是**螢幕上真正顯示的東西** → 抓之前必須把遊戲視窗提到最前面,
     被別的視窗蓋住就會把那個視窗一起抓進來。SetForegroundWindow 對「自己不是前景」的
     process 常常會被 Windows 拒絕,所以先送一個空的 ALT 事件解鎖(Windows 的老規則:
     有輸入事件的 process 才准搶前景)。

  想看的畫面用 -Env 指定開機直達的 dev 變數(FrontendApp 底部那幾個):
      SDO_ROOM=1      開機直接進房間
      SDO_SHOP=1      開機直接開商城
      SDO_JOINDLG=1   開機直接彈「輸入房號」框

  ⚠️ 連線相關的畫面(選男女畫面的三顆鈕、房號框)只有在**真的連上 server** 時才會出現 ——
     連不上會退回單機版面(兩顆鈕)。所以驗那些畫面之前要先把 server 跑起來,
     並確認 <DataRoot>\PROFILE\config.ini 的 [Net] serverAddress 有填。
     (DataRoot 看 repo 根的 data_root.txt,不是 exe 旁邊那份 DATA —— log.txt 第一行會印出來。)

.PARAMETER Exe
  要跑的 dance.exe。預設 <repo>\Build\Windows\dance.exe。

.PARAMETER Out
  輸出的 .png 路徑。預設 <repo>\ui_shot.png。

.PARAMETER Env
  要設的環境變數,格式 KEY=VALUE(可給多個)。

.PARAMETER WaitSec
  啟動後等幾秒才截圖(開機要跑歌庫掃描 + 連線,別給太短)。預設 30。

.PARAMETER KeepOpen
  截完不關遊戲(想自己接著手動點的時候用)。

.EXAMPLE
  ./tools/shoot_ui.ps1 -Out shot_gendersel.png
.EXAMPLE
  ./tools/shoot_ui.ps1 -Env SDO_JOINDLG=1 -Out shot_joindlg.png
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Out,
    [string[]]$Env = @(),
    [int]$WaitSec = 30,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo 'Build\Windows\dance.exe' }
if (-not $Out) { $Out = Join-Path $repo 'ui_shot.png' }
if (-not (Test-Path $Exe)) { throw "找不到 $Exe(先跑 tools\build_windows.ps1)" }

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Sdo -Name Win -MemberDefinition @'
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
'@

foreach ($kv in $Env) {
    $i = $kv.IndexOf('=')
    if ($i -lt 1) { throw "-Env 要 KEY=VALUE,收到:$kv" }
    Set-Item -Path ("Env:" + $kv.Substring(0, $i)) -Value $kv.Substring($i + 1)
    Write-Host ("[shoot] env {0}" -f $kv)
}

Write-Host "[shoot] 啟動 $Exe"
$p = Start-Process -FilePath $Exe -PassThru
$null = $p.Handle          # 先取一次 Handle,否則後面 HasExited/MainWindowHandle 讀不到(.NET 的老陷阱)

try {
    Write-Host "[shoot] 等 $WaitSec 秒讓它開完機(歌庫掃描 + 連線)"
    for ($t = 0; $t -lt $WaitSec; $t++) {
        if ($p.HasExited) { throw "遊戲自己結束了(exit $($p.ExitCode))—— 看 $(Split-Path -Parent $Exe)\log.txt" }
        Start-Sleep -Seconds 1
    }

    $p.Refresh()
    $h = $p.MainWindowHandle
    if ($h -eq [IntPtr]::Zero) { throw "抓不到遊戲視窗(borderless 全螢幕也應該有 handle)" }

    # 空的 ALT 按放:讓本 process 擁有一次輸入事件,Windows 才准我們搶前景。
    [Sdo.Win]::keybd_event(0xA4, 0, 0, [IntPtr]::Zero)
    [Sdo.Win]::keybd_event(0xA4, 0, 2, [IntPtr]::Zero)
    $null = [Sdo.Win]::ShowWindow($h, 9)          # SW_RESTORE
    $null = [Sdo.Win]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 1200                # 給它一幀重畫的時間

    $r = New-Object Sdo.Win+RECT
    if (-not [Sdo.Win]::GetWindowRect($h, [ref] $r)) { throw "GetWindowRect 失敗" }
    $w = $r.R - $r.L; $hh = $r.B - $r.T
    if ($w -le 0 -or $hh -le 0) { throw "視窗尺寸不合理:${w}x${hh}" }
    Write-Host ("[shoot] 視窗 {0}x{1} @ ({2},{3})" -f $w, $hh, $r.L, $r.T)

    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $g.Dispose()
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("[shoot] -> {0} ({1:N0} bytes)" -f $Out, (Get-Item $Out).Length)
}
finally {
    if (-not $KeepOpen -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force; Write-Host "[shoot] 已關閉遊戲" }
}
