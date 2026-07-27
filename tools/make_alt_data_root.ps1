<#
.SYNOPSIS
  Make a second DATA root for testing two clients on one machine — a link farm, not a copy.

.DESCRIPTION
  🔴 config.ini 是**全域一份**(<DataRoot>\PROFILE\config.ini),所以同機開兩份 dance.exe 預設會
  共用同一個 activeId,而且互相 Save() 覆蓋 —— 兩邊會變成同一個角色,測不出多人。
  解法是給第二份 client 一個獨立的 DATA root(環境變數 SDO_DATA_ROOT)。

  但 DATA 有好幾 GB(MUSIC / AVATAR / SCENE),整棵複製既慢又佔空間。這裡改用 **link farm**:
  除了 PROFILE 以外的每個頂層資料夾都用 directory junction 指回原本那棵樹(唯讀資料,共用完全沒問題),
  頂層的檔案用硬連結,只有 PROFILE 是真的複製一份 —— 那才是「身分」所在,必須各自獨立。

  junction(/J)而不是 symlink(/D):junction 不需要管理員權限。

.PARAMETER Source
  原本的 DATA root。預設讀 repo 根的 data_root.txt(通常是 H:\65_remake_clean\DATA)。

.PARAMETER Dest
  要建的第二個 root。預設 H:\sdo_alt_root。

.PARAMETER ActiveId
  第二份 client 要登入的角色(PROFILE 底下的 8 位數資料夾名)。預設 00000001(男),
  與第一份的預設 00000000(女)不同 —— 兩邊看起來就不一樣,一眼分得出誰是誰。

.PARAMETER Force
  Dest 已存在時先刪掉重建(只刪 junction 與 PROFILE 副本,不會動到 Source)。

.EXAMPLE
  ./tools/make_alt_data_root.ps1
  $env:SDO_DATA_ROOT = 'H:\sdo_alt_root'; ./Build/Windows/dance.exe
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Dest = 'H:\sdo_alt_root',
    [string]$ActiveId = '00000001',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $Source) {
    $rootTxt = Join-Path $repo 'data_root.txt'
    if (-not (Test-Path $rootTxt)) { throw "沒有 -Source 也找不到 $rootTxt" }
    $Source = (Get-Content $rootTxt -Raw).Trim()
}
if (-not (Test-Path $Source)) { throw "找不到來源 DATA:$Source" }
Write-Host "[alt] 來源 $Source"

if (Test-Path $Dest) {
    if (-not $Force) { throw "$Dest 已存在(要重建請加 -Force)" }
    # junction 用 Remove-Item -Recurse 會**跟著刪到目標裡的東西** —— 先把 junction 一個一個拆掉再刪外殼。
    Get-ChildItem $Dest -Directory -Force | Where-Object { $_.LinkType -eq 'Junction' } | ForEach-Object {
        & cmd /c rmdir "`"$($_.FullName)`"" | Out-Null
    }
    Remove-Item $Dest -Recurse -Force
    Write-Host "[alt] 已清掉舊的 $Dest"
}

New-Item -ItemType Directory -Force -Path $Dest | Out-Null

$linked = 0; $copied = 0
foreach ($item in Get-ChildItem $Source -Force) {
    $target = Join-Path $Dest $item.Name
    if ($item.PSIsContainer) {
        if ($item.Name -ieq 'PROFILE') { continue }        # 身分:待會真的複製
        & cmd /c mklink /J "`"$target`"" "`"$($item.FullName)`"" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "mklink /J 失敗:$target" }
        $linked++
    } else {
        Copy-Item $item.FullName $target -Force            # 頂層檔案很小(iteminfo/tsv),直接複製
        $copied++
    }
}
Write-Host "[alt] $linked 個資料夾 junction、$copied 個檔案複製"

# PROFILE 真的複製一份 —— config.ini / 角色存檔要各自獨立,否則兩份 client 會互相覆蓋。
$profSrc = Join-Path $Source 'PROFILE'
$profDst = Join-Path $Dest 'PROFILE'
& robocopy $profSrc $profDst /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "PROFILE robocopy 失敗 exit=$LASTEXITCODE" }
Write-Host "[alt] PROFILE 已複製一份(獨立身分)"

# 把第二份的登入角色改掉,讓兩邊看起來不一樣。
$cfg = Join-Path $profDst 'config.ini'
if (Test-Path $cfg) {
    $t = [System.IO.File]::ReadAllText($cfg, [System.Text.Encoding]::UTF8)
    $t = [System.Text.RegularExpressions.Regex]::Replace($t, '(?m)^activeId=.*$', "activeId=$ActiveId")
    [System.IO.File]::WriteAllText($cfg, $t, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "[alt] activeId=$ActiveId"
}

Write-Host ""
Write-Host "第二份 client 這樣跑:"
Write-Host "  `$env:SDO_DATA_ROOT = '$Dest'"
Write-Host "  .\Build\Windows\dance.exe"

# robocopy 「有複製到檔案」就回 1,會被當成本腳本失敗 —— 明確收成 0(上面已經用 -ge 8 判斷過真正的錯誤)。
exit 0
