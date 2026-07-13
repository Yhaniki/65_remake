<#
.SYNOPSIS
  用 junction 組出一個「離線為底、線上補缺」的 DATA root，並把 worktree 指過去。

.DESCRIPTION
  重製版平常的 data root 是離線版 assets/sdox_offline/Extracted。
  離線資料缺很多線上才有的東西（例如 UI/PLAYERINFORMATIONDLG 這整個玩家資訊/戰績對話框），
  所以這裡組一棵 link farm：

      DATA_LINK/            <- 每個 top-level 目錄都是 junction，不佔空間
        AVATAR/  -> 離線 Extracted/AVATAR            (離線優先)
        SCENE/   -> 離線 Extracted/SCENE
        ITEM2D/  -> 線上 DatasSDO/ITEM2D             (離線沒有的才拉線上)
        UI/                                          <- 真目錄，逐個子夾 junction
          ROOM/                 -> 離線 UI/ROOM
          PLAYERINFORMATIONDLG/ -> 線上 UI/PLAYERINFORMATIONDLG
        PROFILE/             <- 真目錄（遊戲會寫存檔，不能 junction 到唯讀來源）

  為什麼不整棵直接指向線上 DatasSDO：
    線上 DatasSDO 的部分 wdance*.mot 是空檔/損毀（見 memory「SDO 舞蹈凍結=壞mot檔」），
    離線 Extracted 的同名檔是好的。所以離線優先，線上只補離線沒有的。

  要知道「到底吃到哪些線上檔」：設 env SDO_TRACE_LOADS=<檔案> 跑一輪，或用
  tools/run_probe_trace.ps1；結果對照 docs/USED_ASSETS.md 更新。

.EXAMPLE
  powershell -File tools/link_data_root.ps1
  powershell -File tools/link_data_root.ps1 -Force     # 重建
#>
[CmdletBinding()]
param(
  [string]$Offline = 'H:\65_remake\assets\sdox_offline\Extracted',
  [string]$Online  = 'H:\65_remake\assets\閉撰敃氪\DatasSDO',
  [string]$Out     = 'H:\65_remake\assets\DATA_LINK',
  [switch]$Force
)

$ErrorActionPreference = 'Stop'

foreach ($p in @($Offline, $Online)) {
  if (-not (Test-Path -LiteralPath $p)) { throw "來源不存在: $p" }
}

function Remove-LinkFarm([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return }
  # junction 必須用 Directory::Delete 拆掉 reparse point，
  # 直接 Remove-Item -Recurse 會沿著 junction 把來源檔一起刪掉。
  Get-ChildItem -LiteralPath $Path -Recurse -Force -Directory |
    Where-Object { $_.LinkType -eq 'Junction' } |
    ForEach-Object { [System.IO.Directory]::Delete($_.FullName) }
  Remove-Item -LiteralPath $Path -Recurse -Force
}

function New-Junction([string]$Link, [string]$Target) {
  if (Test-Path -LiteralPath $Link) { return }
  New-Item -ItemType Junction -Path $Link -Value $Target | Out-Null
}

if ($Force) { Remove-LinkFarm $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$offlineTop = Get-ChildItem -LiteralPath $Offline -Force
$onlineTop  = Get-ChildItem -LiteralPath $Online  -Force
$offlineNames = @{}
foreach ($e in $offlineTop) { $offlineNames[$e.Name.ToUpperInvariant()] = $true }

$linked = New-Object System.Collections.ArrayList

# --- top level：離線優先 ---
foreach ($e in $offlineTop) {
  if ($e.Name -eq 'UI' -or $e.Name -eq 'PROFILE') { continue }
  if ($e.PSIsContainer) {
    New-Junction (Join-Path $Out $e.Name) $e.FullName
    [void]$linked.Add("offline  $($e.Name)/")
  } else {
    Copy-Item -LiteralPath $e.FullName -Destination (Join-Path $Out $e.Name) -Force
    [void]$linked.Add("offline  $($e.Name)")
  }
}

# --- top level：線上補缺 ---
foreach ($e in $onlineTop) {
  if ($e.Name -eq 'UI') { continue }
  if ($offlineNames.ContainsKey($e.Name.ToUpperInvariant())) { continue }
  if ($e.PSIsContainer) {
    New-Junction (Join-Path $Out $e.Name) $e.FullName
    [void]$linked.Add("ONLINE   $($e.Name)/")
  } else {
    Copy-Item -LiteralPath $e.FullName -Destination (Join-Path $Out $e.Name) -Force
    [void]$linked.Add("ONLINE   $($e.Name)")
  }
}

# --- UI：真目錄 + 逐個子夾 junction（離線優先，線上補缺）---
$uiOut = Join-Path $Out 'UI'
New-Item -ItemType Directory -Force -Path $uiOut | Out-Null

$offUiNames = @{}
foreach ($e in Get-ChildItem -LiteralPath (Join-Path $Offline 'UI') -Force -Directory) {
  $offUiNames[$e.Name.ToUpperInvariant()] = $true
  New-Junction (Join-Path $uiOut $e.Name) $e.FullName
  [void]$linked.Add("offline  UI/$($e.Name)/")
}
foreach ($e in Get-ChildItem -LiteralPath (Join-Path $Online 'UI') -Force -Directory) {
  if ($offUiNames.ContainsKey($e.Name.ToUpperInvariant())) { continue }
  New-Junction (Join-Path $uiOut $e.Name) $e.FullName
  [void]$linked.Add("ONLINE   UI/$($e.Name)/")
}
foreach ($src in @((Join-Path $Online 'UI'), (Join-Path $Offline 'UI'))) {
  foreach ($f in Get-ChildItem -LiteralPath $src -Force -File) {
    Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $uiOut $f.Name) -Force
  }
}

# --- PROFILE：真目錄，遊戲要寫回 ---
$profOut = Join-Path $Out 'PROFILE'
if (-not (Test-Path -LiteralPath $profOut)) {
  $profSrc = Join-Path $Offline 'PROFILE'
  if (Test-Path -LiteralPath $profSrc) {
    Copy-Item -LiteralPath $profSrc -Destination $profOut -Recurse -Force
  } else {
    New-Item -ItemType Directory -Force -Path $profOut | Out-Null
  }
}

# --- 健檢：SdoExtracted.LooksLikeGameDataRoot 認不認得 ---
if (-not (Test-Path -LiteralPath (Join-Path $Out 'AVATAR\FEMALE.HRC'))) {
  Write-Warning '找不到 AVATAR/FEMALE.HRC，SdoExtracted 可能不認這個 root'
}
if (-not (Test-Path -LiteralPath (Join-Path $Out 'UI\PLAYERINFORMATIONDLG\PLAYERINFORMATIONDLG.XML'))) {
  Write-Warning '找不到 UI/PLAYERINFORMATIONDLG，玩家資訊面板會缺素材'
}

# --- 把 worktree 指過去 ---
$repo = Split-Path -Parent $PSScriptRoot
$rootTxt = Join-Path $repo 'data_root.txt'
Set-Content -LiteralPath $rootTxt -Value $Out -Encoding UTF8 -NoNewline

$online = @($linked.ToArray() | Where-Object { $_ -like 'ONLINE*' })
Write-Host ''
Write-Host "DATA root  : $Out"
Write-Host "指向設定檔 : $rootTxt"
Write-Host ''
Write-Host "共 $($linked.Count) 個項目，其中 $($online.Count) 個來自線上 DatasSDO"
foreach ($l in $online) { Write-Host ('  ' + $l) }
