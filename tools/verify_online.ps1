#Requires -Version 5.1
<#
.SYNOPSIS
  兩台 client 的線上驗證:同步進場、同場多舞者、隨機值一致 —— 而且可以順便驗 TLS 與 token。

.DESCRIPTION
  單元測試證明「規則對不對」,這支腳本證明「線接對了沒有」。歷次實機驗證抓到的缺陷
  (絕對路徑塞進 wire 欄位、NetConnection 關掉之後不能重用、file 連線忘了送密碼…)
  單元測試一條都沒紅過 —— 全部是接線,而接線只有真的跑起來才看得到。

  做法:一台 server + 兩台 dance.exe,兩台各用自己的 DATA root(SDO_DATA_ROOT)。
  🔴 兩台都用 alt root,**絕不碰玩家真正的 DATA\PROFILE\config.ini** ——
     這支腳本會改寫 [Net] 那幾個鍵,寫壞玩家的設定是不能接受的代價。

  驗完會把兩邊 log 撈出來的證據列成一張表。每一條都是「看得到 / 看不到」,不是「大概有吧」。

.PARAMETER Tls
  順便驗 TLS:自簽一張憑證、從 server 的開機輸出抓指紋、填進兩台 client 的 config.ini。
  (指紋刻意從 server 的**輸出**抓而不是自己算 —— 那一行是玩家要複製的東西,它印錯就等於功能壞了。)

.PARAMETER Token
  順便驗 token 認證:產兩個 token 綁到兩個 playerId,server 帶 --tokens。

.EXAMPLE
  pwsh -File tools\verify_online.ps1
  pwsh -File tools\verify_online.ps1 -Tls -Token -Seconds 90
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Server,
    [switch]$Tls,
    [switch]$Token,
    # 滿房模式:房主把自己以外的座位全部關掉(SDO_CLOSESEATS),第二台加入時會撞到 full。
    # 驗的是「座位滿了自動改用旁觀身分進去」,所以不打歌 —— 檢查表也只驗到進房那一段。
    [switch]$FullRoom,
    # 從「開兩台」到「撈 log」總共等多久(秒)。要跑完一首歌的話給 120 以上。
    [int]$Seconds = 150,
    # 房主要選的歌(外部歌的標題片段;SDO_PICKSONG 就吃這個)。兩台共用同一棵 ADDON → 都有這首,
    # 所以不會混進「缺歌傳檔」那條路徑(那條 M5 已經單獨驗過了)。
    [string]$Song = 'Bassdrop',
    [int]$Port = 27099,
    [string]$RootA = 'H:\sdo_verify_a',
    [string]$RootB = 'H:\sdo_verify_b',
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe)    { $Exe    = Join-Path $repo 'Build\Windows\dance.exe' }
if (-not $Server) { $Server = Join-Path $repo 'server\Sdo.Server\bin\Debug\net8.0\sdo-server.exe' }
if (-not $Out)    { $Out    = Join-Path $repo 'online_verify.md' }
if (-not (Test-Path $Exe))    { throw "找不到 $Exe(先跑 tools\build_windows.ps1)" }
if (-not (Test-Path $Server)) { throw "找不到 $Server(先跑 dotnet build server/)" }

$work    = Join-Path $repo '.verify_online'
$srvData = Join-Path $work 'srvdata'
$srvLog  = Join-Path $work 'server.log'
$pfx     = Join-Path $work 'test.pfx'
$tokFile = Join-Path $work 'tokens.txt'
$Password = 'abab123'
$TokenA   = '0123456789abcdef0123456789abcde0'
$TokenB   = '0123456789abcdef0123456789abcde1'

function Kill-All {
    # 殘留的 dance.exe 會改寫 config.ini 並抹掉 [Net](踩過兩次)。server 也要先關,不然搶 port。
    foreach ($n in @('dance', 'sdo-server')) {
        Get-Process $n -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "[verify] 關掉殘留的 $n pid $($_.Id)"
            try { Stop-Process -Id $_.Id -Force } catch { }
        }
    }
    Start-Sleep -Milliseconds 500
}

# config.ini 的 [Net] 就位。**只改這幾個鍵**,其餘保持原樣(RoomConfig.Load 會自己補齊缺的)。
function Set-NetConfig([string]$root, [hashtable]$kv) {
    $path = Join-Path $root 'PROFILE\config.ini'
    if (-not (Test-Path $path)) { throw "找不到 $path(alt root 沒建好?)" }
    $lines = [System.Collections.Generic.List[string]](Get-Content -LiteralPath $path -Encoding UTF8)
    foreach ($k in $kv.Keys) {
        $hit = $false
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "^$([regex]::Escape($k))=") { $lines[$i] = "$k=$($kv[$k])"; $hit = $true }
        }
        if (-not $hit) { $lines.Add("$k=$($kv[$k])") }
    }
    Set-Content -LiteralPath $path -Value $lines -Encoding UTF8
}

function New-AltRoot([string]$dest, [string]$activeId) {
    if (Test-Path $dest) { return }
    & (Join-Path $PSScriptRoot 'make_alt_data_root.ps1') -Dest $dest -ActiveId $activeId | Out-Null
}

function Find-OpenSsl {
    $c = Get-Command openssl -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @("$env:ProgramFiles\Git\usr\bin\openssl.exe", "$env:ProgramFiles\Git\mingw64\bin\openssl.exe")) {
        if (Test-Path $p) { return $p }
    }
    throw '找不到 openssl(Git for Windows 有附一份)'
}

# ---- 準備 ----

Kill-All
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force -Path $work, $srvData | Out-Null

Write-Host '[verify] 準備兩份 DATA root(link farm,不是複製)…'
New-AltRoot $RootA '00000000'   # 女
New-AltRoot $RootB '00000001'   # 男 —— 兩邊外觀不同,截圖一眼分得出誰是誰

$srvArgs = @('--port', $Port, '--bind', '127.0.0.1', '--data', $srvData, '--password', $Password, '-v')

if ($Tls) {
    Write-Host '[verify] 自簽一張憑證…'
    $ssl = Find-OpenSsl
    $key = Join-Path $work 'key.pem'; $crt = Join-Path $work 'cert.pem'
    # ⚠️ openssl 把進度寫到 stderr,而 PowerShell 5.1 會把原生程式的 stderr 包成 ErrorRecord ——
    # 在 $ErrorActionPreference='Stop' 下那會直接中斷整個腳本(而它其實成功了)。
    # 所以這一段把偏好調成 Continue,成敗改看「檔案出來了沒有」。
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $ssl req -x509 -newkey rsa:2048 -sha256 -days 30 -nodes -keyout $key -out $crt `
            -subj '/CN=localhost' -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' | Out-Null
        if (-not (Test-Path $crt)) { throw 'openssl 產憑證失敗' }
        & $ssl pkcs12 -export -out $pfx -inkey $key -in $crt -passout 'pass:' | Out-Null
    }
    finally { $ErrorActionPreference = $prev }
    if (-not (Test-Path $pfx)) { throw 'openssl 產 pfx 失敗' }
    $srvArgs += @('--tls-cert', $pfx)
}

if ($Token) {
    # token 綁 playerId → 冒用身分在啟用之後就沒用了(那是整個機制的重點)。
    Set-Content -LiteralPath $tokFile -Encoding UTF8 -Value @(
        "# verify_online.ps1 產的測試 token",
        "$TokenA = 00000000, 驗證女",
        "$TokenB = 00000001, 驗證男")
    $srvArgs += @('--tokens', $tokFile)
}

Write-Host "[verify] 開 server: $Server $($srvArgs -join ' ')"
$srv = Start-Process -FilePath $Server -ArgumentList $srvArgs -PassThru `
    -RedirectStandardOutput $srvLog -RedirectStandardError (Join-Path $work 'server.err.log') -WindowStyle Hidden

# 等它印出「監聽中」(順便就是在等憑證載入 —— 憑證有問題 server 會直接結束)
$fp = ''
$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    if ($srv.HasExited) { Get-Content $srvLog -Tail 20; throw "server 直接結束了(exit $($srv.ExitCode))" }
    $txt = if (Test-Path $srvLog) { Get-Content -LiteralPath $srvLog -Raw -Encoding UTF8 } else { '' }
    if ($txt -match '監聽中') {
        if ($Tls) {
            # 指紋是玩家要複製的那一串 → 從 server 的輸出抓,不要自己算。
            $m = [regex]::Match($txt, '(?m)^\[sdo-server\]\s+([0-9a-f]{64})\s*$')
            if ($m.Success) { $fp = $m.Groups[1].Value }
        }
        if (-not $Tls -or $fp) { break }
    }
    Start-Sleep -Milliseconds 250
}
if ($Tls -and -not $fp) { throw 'server 沒印出憑證指紋 —— client 沒東西可以釘選' }
if ($fp) { Write-Host "[verify] 憑證指紋 $fp" }

# 🔴 完奏模式關掉。這首歌的 mp3 有 5 分鐘,而譜面只到 1:54 —— 開著完奏的話 gameplay 會跑滿
# 五分鐘才結算,整支驗證要等超過六分鐘(第一次跑就是這樣逾時的,看起來像結算壞了)。
$net = @{
    opt_playFullSong = 0
    serverAddress = '127.0.0.1'
    serverPort    = $Port
    serverPassword = $Password
    serverTls     = $(if ($Tls) { 1 } else { 0 })
    serverCertFingerprint = $fp
    netAutoDownload = 1
}
Set-NetConfig $RootA ($net + @{ serverToken = $(if ($Token) { $TokenA } else { '' }) })
Set-NetConfig $RootB ($net + @{ serverToken = $(if ($Token) { $TokenB } else { '' }) })

# ---- 開兩台 client ----
# A = 房主(SDO_ROOM 直達房間、SDO_PICKSONG 挑一首官方歌、SDO_AUTOSTART 兩人到位就開始)
# B = 客人(SDO_JOINFIRST 加入第一間房、SDO_AUTOREADY 自動按準備)
# 兩台都 SDO_AUTOPLAY(自動打完,不需要人在鍵盤前面)+ SDO_VERBOSE(打包版預設把 Debug.Log 全丟掉)
function Start-Client([string]$root, [hashtable]$extra, [string]$label) {
    foreach ($k in @('SDO_ROOM','SDO_JOINFIRST','SDO_AUTOREADY','SDO_AUTOSTART','SDO_AUTOPLAY','SDO_PICKSONG','SDO_VERBOSE','SDO_DATA_ROOT','SDO_DANCERS','SDO_ROOMAVATARS','SDO_LOG')) {
        Set-Item -Path "env:$k" -Value ''
    }
    $env:SDO_VERBOSE = '1'
    $env:SDO_DATA_ROOT = $root
    $env:SDO_LOG = $extra['__log']
    foreach ($k in $extra.Keys) { if ($k -ne '__log') { Set-Item -Path "env:$k" -Value $extra[$k] } }
    Write-Host "[verify] 開 client $label (root=$root)"
    return Start-Process -FilePath $Exe -PassThru
}

$logA = Join-Path $work 'clientA.log'
$logB = Join-Path $work 'clientB.log'
$envA = @{ SDO_ROOM = '1'; SDO_PICKSONG = $Song; SDO_AUTOPLAY = '1'; __log = $logA }
if ($FullRoom) { $envA['SDO_CLOSESEATS'] = '1' } else { $envA['SDO_AUTOSTART'] = '1' }
$pA = Start-Client $RootA $envA 'A(房主)'
Start-Sleep -Seconds 12   # 讓 A 先進到房間、拿到房號
$pB = Start-Client $RootB @{ SDO_JOINFIRST = '1'; SDO_AUTOREADY = '1'; SDO_AUTOPLAY = '1'; __log = $logB } 'B(客人)'

# 等到 server 印出「第 N 場結算」為止 —— 一首歌多長不該由腳本猜(猜短了整場驗證就少了結算那一段)。
Write-Host "[verify] 等這一場打完(最多 $Seconds 秒)…"
$deadline = (Get-Date).AddSeconds($Seconds)
$done = $false
$waitFor = if ($FullRoom) { '以旁觀身分進入房 \d+' } else { '房 \d+:第 \d+ 場結算' }
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $peek = if (Test-Path $srvLog) { Get-Content -LiteralPath $srvLog -Raw -Encoding UTF8 } else { '' }
    if ($peek -match $waitFor) { $done = $true; break }
    if ($pA.HasExited -or $pB.HasExited) { Write-Host '[verify] 有一台 client 自己結束了'; break }
}
if (-not $done) { Write-Host "[verify] ⚠️ 等到逾時還沒看到結算" }

# 兩台各自寫自己的 log(SDO_LOG)。**不能共用** —— 兩份 dance.exe 寫同一個 <exeDir>\log.txt
# 時,後開的那台在啟動時就把前一台的搬成 .prev(等於清掉),之後還會交錯寫。

Kill-All

# ---- 撈證據 ----

function Read-Log([string]$p) { if (Test-Path $p) { return (Get-Content -LiteralPath $p -Raw -Encoding UTF8) } return '' }
$srvTxt = Read-Log $srvLog
$cliA   = Read-Log $logA
$cliB   = Read-Log $logB

function Check([string]$name, [bool]$ok, [string]$note) {
    $mark = 'FAIL'; if ($ok) { $mark = 'OK  ' }
    Write-Host "[$mark] $name  $note"
    return [pscustomobject]@{ Name = $name; Ok = $ok; Note = $note }
}

$rows = @()
$rows += Check '兩台都連上 server' ($srvTxt -match 'user 1 .*上線' -and $srvTxt -match 'user 2 .*上線') ''
if ($Tls) {
    # (TLS,共 N 條) 是每條連線註冊時印的 —— 兩台 client 至少兩條,而且不能有任何一條是明文。
    $tlsConns = ([regex]::Matches($srvTxt, '\(TLS,共')).Count
    $rows += Check 'TLS:每一條連線都是加密的' ($tlsConns -ge 2 -and $srvTxt -notmatch '\(明文,共') "加密連線 $tlsConns 條"
}
if ($Token) {
    $rows += Check 'token:啟用且沒有一條被拒' `
        ($srvTxt -match 'token 認證已啟用:2 個 token' -and $srvTxt -notmatch 'token 認證失敗') ''
    # 身分由 server 決定 → 名字是 token 檔綁的那兩個,不是 client 自稱的
    $rows += Check 'token:身分由 server 覆寫' `
        ($srvTxt -match 'user \d+ 「驗證女」上線' -and $srvTxt -match 'user \d+ 「驗證男」上線') ''
}
if ($FullRoom) {
    # ---- 滿房 → 自動旁觀(使用者要求的行為)----
    $rows += Check '房主把其他座位都關了' ($cliA -match 'SDO_CLOSESEATS:把座位') ([regex]::Match($cliA, '\[dev\] SDO_CLOSESEATS[^
]*').Value)
    $rows += Check '第二台**沒有**坐到位子' ($srvTxt -notmatch 'user 2 加入房') ''
    $rows += Check '第二台改用旁觀身分進了房' ($srvTxt -match 'user \d+ 以旁觀身分進入房 \d+') ([regex]::Match($srvTxt, 'user \d+ 以旁觀身分進入房[^
]*').Value)
    $rows += Check 'client 端知道自己是旁觀進去的' ($cliB -match 'SDO_JOINFIRST:進了房 \d+' -and $cliB -match '旁觀身分') ([regex]::Match($cliB, '\[dev\] SDO_JOINFIRST[^
]*').Value)
    $rows += Check 'A 沒有例外' ($cliA -notmatch 'EXC |NullReferenceException') ''
    $rows += Check 'B 沒有例外' ($cliB -notmatch 'EXC |NullReferenceException') ''
} else {
$rows += Check '開房' ($srvTxt -match 'user \d+ 開了房 \d{5}') ([regex]::Match($srvTxt, 'user \d+ 開了房 \d+').Value)
$rows += Check '第二台加入同一間房' ($srvTxt -match 'user \d+ 加入房 \d{5} 座位 \d') ([regex]::Match($srvTxt, 'user \d+ 加入房[^
]*').Value)
$rows += Check '房主換歌廣播出去' ($srvTxt -match '房 \d+ 換歌:') ([regex]::Match($srvTxt, '房 \d+ 換歌:[^
]*').Value)
$rows += Check '同步進場:server 開場' ($srvTxt -match '房 \d+:第 \d+ 場開始') ([regex]::Match($srvTxt, '房 \d+:第 \d+ 場開始').Value)

# 隨機值一致:兩台的 resolved 那行要逐字相同(場景/隊形/組隊版型/隨機難度都在裡面)
$resA = [regex]::Match($cliA, '\[net\] resolved [^
]*').Value.Trim()
$resB = [regex]::Match($cliB, '\[net\] resolved [^
]*').Value.Trim()
# spectator=/dancers= 之外的部分才是「該一樣」的(旁觀身分本來就可能不同)
$keyA = ($resA -replace ' spectator=\w+', '')
$keyB = ($resB -replace ' spectator=\w+', '')
$rows += Check '兩台拿到同一份 resolved(場景/隊形一致)' ($keyA -ne '' -and $keyA -eq $keyB) "A: $resA / B: $resB"

$dA = [regex]::Match($cliA, '\[dancers\] 生出[^
]*').Value
$dB = [regex]::Match($cliB, '\[dancers\] 生出[^
]*').Value
$rows += Check '同場多舞者(兩台都生出別人)' ($dA -match '生出 [1-9]' -and $dB -match '生出 [1-9]') "A: $dA / B: $dB"

$rows += Check '分數流:server 收到 frame 並彙整' ($srvTxt -match 'frame') ''
$rows += Check '結算' ($srvTxt -match '房 \d+:第 \d+ 場結算') ([regex]::Match($srvTxt, '房 \d+:第 \d+ 場結算').Value)
$rows += Check 'A 沒有例外' ($cliA -notmatch 'EXC |NullReferenceException') ''
$rows += Check 'B 沒有例外' ($cliB -notmatch 'EXC |NullReferenceException') ''
}

$md = @()
$md += '# 線上實機驗證'
$md += ''
# ⚠️ dance.exe 的時間戳沒有參考價值:Unity 不會重寫那個 launcher(內容沒變就不動它)。
# 真正代表「這份 build 有多新」的是編出來的 managed dll。
$stampFile = Join-Path (Split-Path -Parent $Exe) 'dance_Data\Managed\Sdo.UI.dll'
if (-not (Test-Path $stampFile)) { $stampFile = $Exe }
$md += "* build: ``$Exe``  ($( (Get-Item $stampFile).LastWriteTime ))"
$md += "* server: ``$($srvArgs -join ' ')``"
$tlsNote = if ($Tls) { "開($fp)" } else { '關' }
$tokNote = if ($Token) { '開' } else { '關' }
$md += "* TLS: $tlsNote / token: $tokNote"
$md += ''
$md += '| 檢查 | 結果 | 證據 |'
$md += '|---|---|---|'
foreach ($r in $rows) {
    $v = if ($r.Ok) { 'OK' } else { '**FAIL**' }
    $md += "| $($r.Name) | $v | $($r.Note) |"
}
$md += ''
$md += "log:``$srvLog`` / ``$logA`` / ``$logB``"
Set-Content -LiteralPath $Out -Value $md -Encoding UTF8
Write-Host "[verify] 報告寫到 $Out"

if ($rows | Where-Object { -not $_.Ok }) { exit 1 }
