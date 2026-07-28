<#
.SYNOPSIS
  兩開 dance.exe 驗「頭上聊天泡的前後景遮擋」,兩邊各截一張圖。

.DESCRIPTION
  泡的畫已經搬進房間相機(layer RoomBubble),要靠 GPU 深度測試才會被人擋住。
  這件事沒有辦法從腳本輸出判斷 —— 只能實機截圖看。這支腳本把那個迴圈自動化:

    server 起來 → A 開房(SDO_ROOM=1) → B 加入(SDO_JOINFIRST=1)
    → 兩邊各「點空曠處 → 打字 → Enter」讓頭上出現泡
    → B 按方向鍵走幾步(走到 A 前面/後面)
    → 兩個視窗各截一張

  怎麼打字:泡的打字模式是「在房間空曠處按左鍵」觸發的(HandleRoomBlankChatClick),
  所以先 SetCursorPos 到畫面中央偏下的空地再 click,然後用 keybd_event 打 ASCII、Enter 送出。

  ⚠️ 截圖走 GDI CopyFromScreen(抓螢幕上真正顯示的東西)→ 抓之前一定要把那個視窗提到最前面,
     而 SetForegroundWindow 對非前景 process 常被 Windows 拒絕 → 先送一個空的 ALT 事件解鎖。
     兩個視窗會疊在一起,所以是「提 A → 抓 A → 提 B → 抓 B」,不是同時抓。

  ⚠️ 兩份 client 必須各用一份 DATA 根,否則共用同一個 activeId/config.ini 會互相覆蓋。
     B 走 -AltRoot(tools\make_alt_data_root.ps1 建的 junction farm)。
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$AltRoot = 'H:\sdo_alt_root',
    [string]$OutA,
    [string]$OutB,
    [int]$BootSecA = 48,
    [int]$BootSecB = 40,
    [string]$SayA = 'HOST TALKING',
    [string]$SayB = 'GUEST TALKING',
    [int]$WalkMs = 900,
    [string]$WalkKey = 'Down',
    [switch]$M3,
    [switch]$M4,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe)  { $Exe  = Join-Path $repo 'Build\Windows\dance.exe' }
if (-not $OutA) { $OutA = Join-Path $repo 'bubble_host.png' }
if (-not $OutB) { $OutB = Join-Path $repo 'bubble_guest.png' }
if (-not (Test-Path $Exe)) { throw "找不到 $Exe(先跑 tools\build_windows.ps1)" }

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Sdo -Name W -MemberDefinition @'
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
'@

function Focus-Win([IntPtr]$h) {
    [Sdo.W]::keybd_event(0xA4, 0, 0, [IntPtr]::Zero)      # 空的 ALT:讓本 process 有輸入事件才准搶前景
    [Sdo.W]::keybd_event(0xA4, 0, 2, [IntPtr]::Zero)
    $null = [Sdo.W]::ShowWindow($h, 9)                    # SW_RESTORE
    $null = [Sdo.W]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 700
}

function Get-Rect([IntPtr]$h) {
    $r = New-Object Sdo.W+RECT
    if (-not [Sdo.W]::GetWindowRect($h, [ref] $r)) { throw 'GetWindowRect 失敗' }
    return $r
}

# 🔴 一定要帶 scan code(MapVirtualKey)。只給 vk、scan=0 的話某些讀鍵路徑收不到字 ——
#    實測:聊天泡進得去打字模式、游標也在閃,但一個字都沒進去,看起來像「輸入框壞了」。
function Tap([byte]$vk, [int]$holdMs = 55) {
    $sc = [byte]([Sdo.W]::MapVirtualKey([uint32]$vk, 0))
    [Sdo.W]::keybd_event($vk, $sc, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds $holdMs
    [Sdo.W]::keybd_event($vk, $sc, 2, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 60
}

function Hold([byte]$vk, [int]$ms) {
    $sc = [byte]([Sdo.W]::MapVirtualKey([uint32]$vk, 0))
    [Sdo.W]::keybd_event($vk, $sc, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds $ms
    [Sdo.W]::keybd_event($vk, $sc, 2, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 200
}

# ASCII → 虛擬鍵。只支援大寫字母/數字/空白(訊息內容自己挑,不需要中文 —— 中文要走 IME,那是另一回事)。
function Type-Ascii([string]$s) {
    foreach ($ch in $s.ToCharArray()) {
        if ($ch -eq ' ') { Tap 0x20 }
        elseif ($ch -match '[A-Z]') { Tap ([byte][char]$ch) }
        elseif ($ch -match '[0-9]') { Tap ([byte][char]$ch) }
        else { Write-Host "[shoot] 跳過不支援的字元 '$ch'" }
    }
}

function Say([IntPtr]$h, [string]$msg) {
    $r = Get-Rect $h
    # 房間空曠處:畫面中央偏下(左下訊息欄與右側面板都不在這裡),點它會進「頭上泡打字模式」。
    $x = $r.L + [int](($r.R - $r.L) * 0.42)
    $y = $r.T + [int](($r.B - $r.T) * 0.62)
    $null = [Sdo.W]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 250
    [Sdo.W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
    [Sdo.W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
    Start-Sleep -Milliseconds 600
    Type-Ascii $msg
    Start-Sleep -Milliseconds 250
    Tap 0x0D                                                # Enter = 送出
    Start-Sleep -Milliseconds 900
}


# 800×600 設計座標 → 螢幕座標。用 **client 區**而不是 window rect:後者含標題列與邊框,
# 拿它換算會整體偏掉一個標題列的高度(點不到想點的東西,而且偏移量看起來像「座標算錯」)。
function Design-ToScreen([IntPtr]$h, [double]$dx, [double]$dy) {
    $c = New-Object Sdo.W+RECT
    if (-not [Sdo.W]::GetClientRect($h, [ref] $c)) { throw 'GetClientRect 失敗' }
    $o = New-Object Sdo.W+POINT
    $o.X = 0; $o.Y = 0
    if (-not [Sdo.W]::ClientToScreen($h, [ref] $o)) { throw 'ClientToScreen 失敗' }
    $w = $c.R - $c.L; $ht = $c.B - $c.T
    return @([int]($o.X + $dx / 800.0 * $w), [int]($o.Y + $dy / 600.0 * $ht))
}

function Click-At([IntPtr]$h, [double]$dx, [double]$dy, [switch]$Right) {
    $p = Design-ToScreen $h $dx $dy
    $null = [Sdo.W]::SetCursorPos($p[0], $p[1])
    Start-Sleep -Milliseconds 260
    if ($Right) {
        [Sdo.W]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero)   # RIGHTDOWN
        [Sdo.W]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)   # RIGHTUP
    } else {
        [Sdo.W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
        [Sdo.W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
    }
    Start-Sleep -Milliseconds 500
}

function DoubleClick-At([IntPtr]$h, [double]$dx, [double]$dy) {
    $p = Design-ToScreen $h $dx $dy
    $null = [Sdo.W]::SetCursorPos($p[0], $p[1])
    Start-Sleep -Milliseconds 260
    for ($i = 0; $i -lt 2; $i++) {
        [Sdo.W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
        [Sdo.W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 90        # < Unity EventSystem 的 clickCount 視窗(0.3s)
    }
    Start-Sleep -Milliseconds 600
}

function Shoot([IntPtr]$h, [string]$out) {
    $r = Get-Rect $h
    $w = $r.R - $r.L; $hh = $r.B - $r.T
    if ($w -le 0 -or $hh -le 0) { throw "視窗尺寸不合理:${w}x${hh}" }
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $g.Dispose()
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("[shoot] -> {0} ({1}x{2})" -f $out, $w, $hh)
}

$srv = $null; $pa = $null; $pb = $null
try {
    $srvExe = Join-Path $repo 'server\Sdo.Server\bin\Release\net8.0\sdo-server.exe'
    if (Test-Path $srvExe) {
        Write-Host '[shoot] 啟動 server'
        $srv = Start-Process -FilePath $srvExe -PassThru -WindowStyle Minimized
        $null = $srv.Handle
        Start-Sleep -Seconds 2
    } else { Write-Host "[shoot] 找不到 server exe($srvExe)—— 假設外面已經有一台在跑" }

    $logSrc = Join-Path (Split-Path -Parent $Exe) 'log.txt'
    $env:SDO_DATA_ROOT = ''
    if ($M4) { $SayA = ''; $SayB = '' }   # 見下:自動說話會讓聊天框 armed,把 F2 擋掉
    $env:SDO_ROOM = '1'; $env:SDO_JOINFIRST = ''; $env:SDO_SAY = $SayA
    Write-Host '[shoot] 啟動 A(房主,主 DATA 根)'
    $pa = Start-Process -FilePath $Exe -PassThru
    $null = $pa.Handle
    Start-Sleep -Seconds $BootSecA
    # 兩份 client 寫同一個 log.txt(在 exe 旁邊)→ B 一啟動就會把 A 的蓋掉。先留一份。
    if (Test-Path $logSrc) { Copy-Item $logSrc (Join-Path $repo 'bubble_logA.txt') -Force }

    $env:SDO_ROOM = ''; $env:SDO_JOINFIRST = '1'; $env:SDO_DATA_ROOT = $AltRoot; $env:SDO_SAY = $SayB
    if ($M4) { $env:SDO_AUTOREADY = '1' }   # 非房主自動按準備(見 RoomScreen.TickDevAutoReady)
    Write-Host "[shoot] 啟動 B(加入,DATA 根 = $AltRoot)"
    $pb = Start-Process -FilePath $Exe -PassThru
    $null = $pb.Handle
    Start-Sleep -Seconds $BootSecB
    $env:SDO_DATA_ROOT = ''; $env:SDO_JOINFIRST = ''; $env:SDO_SAY = ''; $env:SDO_AUTOREADY = ''
    if (Test-Path $logSrc) { Copy-Item $logSrc (Join-Path $repo 'bubble_logB.txt') -Force }

    $pa.Refresh(); $pb.Refresh()
    $ha = $pa.MainWindowHandle; $hb = $pb.MainWindowHandle
    if ($ha -eq [IntPtr]::Zero -or $hb -eq [IntPtr]::Zero) { throw '抓不到兩個遊戲視窗' }

    # B 先走幾步:走到與 A 不同的深度,才有「一個人在另一個人前面」可看。
    $vk = @{ 'Up' = 0x26; 'Down' = 0x28; 'Left' = 0x25; 'Right' = 0x27 }[$WalkKey]
    if (-not $vk) { throw "-WalkKey 只接受 Up/Down/Left/Right" }
    Focus-Win $hb
    Write-Host "[shoot] B 按 $WalkKey $WalkMs ms"
    Hold ([byte]$vk) $WalkMs

    if ($M4) {
        # 同步進場:B 先按「準備」(右下大圓鈕),A 再按 F2(= 按開始)。
        # 兩邊都應該在同一刻解除 loading 畫面 → 打一小段 → 截圖看右側名單有沒有兩個人的分數。
        Write-Host '[shoot] 等 B 自動按準備(SDO_AUTOREADY)'
        Start-Sleep -Seconds 6
        Write-Host '[shoot] A 按開始(F2)'
        Focus-Win $ha; Tap 0x71          # F2 = 直接開始(RoomScreen.Update 的捷徑)
        Start-Sleep -Seconds 25          # 等 loading + 開場 READY/GO + 打一小段
        Shoot $ha (Join-Path $repo 'm4_play_host.png')
        Focus-Win $hb; Shoot $hb (Join-Path $repo 'm4_play_guest.png')
        Start-Sleep -Seconds 20          # 再等一段,看分數有沒有在跑
        Focus-Win $ha; Shoot $ha (Join-Path $repo 'm4_play_host2.png')
        return
    }

    if ($M3) {
        # 六格頭貼:HeadSlotX={63,184,306,430,549,675} / HeadSlotY=56 / 96x76(RoomLayout)。
        # B 會坐第 2 格(index 1)—— 房主在 0,join 取 index 最小的 Open 位子。
        $slotCx = 184 + 48; $slotCy = 56 + 38
        Write-Host '[shoot] A 右鍵 B 的座位 → 選單'
        Focus-Win $ha
        Click-At $ha $slotCx $slotCy -Right
        Shoot $ha (Join-Path $repo 'm3_menu.png')

        # 關掉選單(點畫面別處),再用**雙擊鎖格**驗一次真的座位操作。
        # 為什麼不點選單裡的「踢出玩家」:那要另外算出選單列的位置,而 Design-ToScreen 目前
        # 有一個還沒查清的水平偏移(選單本身有出現、位置也對得上點擊點,所以座位命中是對的)。
        # 雙擊只需要座位中心 —— 而那個座標已經被上面的右鍵證明是對的。
        # 鎖格會讓 server 先把那個人踢掉再關位子(R8),所以這一步同時驗到「踢人」與「關位子」。
        Write-Host '[shoot] 關選單 → 雙擊 B 的座位(鎖格 = 踢人 + 關位)'
        Click-At $ha 400 520
        Start-Sleep -Milliseconds 400
        DoubleClick-At $ha $slotCx $slotCy
        Start-Sleep -Seconds 2
        Shoot $ha (Join-Path $repo 'm3_after_kick_host.png')
        Focus-Win $hb; Shoot $hb (Join-Path $repo 'm3_after_kick_guest.png')
        return
    }

    Write-Host '[shoot] 等 SDO_SAY 各說一次(泡在壽命內)'
    Start-Sleep -Seconds 5

    Focus-Win $ha; Shoot $ha $OutA
    Focus-Win $hb; Shoot $hb $OutB
}
finally {
    if (-not $KeepOpen) {
        foreach ($p in @($pa, $pb, $srv)) {
            if ($p -ne $null -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
        }
        Write-Host '[shoot] 已關閉'
    }
}
