<#
.SYNOPSIS
  Assemble the clean ship layout for a built player: all SDO game data under a single DATA/ folder beside the exe.

.DESCRIPTION
  Run AFTER a Unity Windows build (BuildScript.cs calls this automatically, or run it by hand).
  Produces, inside -BuildDir:

      dance.exe + *.dll + <exe>_Data/ + MonoBleedingEdge/   (Unity engine, left at top level)
      screensave/                                           (screenshot output, beside the exe)
      DATA/                                                 (SdoExtracted.Root)
        <Extracted contents> + SE/ + BGM/ + MUSIC/ + REPLAY/
        UI/MUSIC/ICONS      <- overlaid with the FULL online (DatasSDO) icon set
        UI/STATIS/STATISTIC <- overlaid with the online result-screen art (safety; usually already in Extracted)

  All source paths derive from the repo root ($PSScriptRoot\..) — no hardcoded drive letters.

.PARAMETER BuildDir
  The build output folder containing the exe. Default: <repo>\Build\Windows.
#>
[CmdletBinding()]
param(
    [string]$BuildDir
)

$ErrorActionPreference = 'Stop'

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $BuildDir) { $BuildDir = Join-Path $Repo 'Build\Windows' }
$Off  = Join-Path $Repo 'assets\sdox_offline'
$Data = Join-Path $BuildDir 'DATA'

Write-Host "[package] repo     = $Repo"
Write-Host "[package] buildDir = $BuildDir"
Write-Host "[package] data     = $Data"

if (-not (Test-Path $BuildDir)) { throw "BuildDir not found: $BuildDir (build the player first)" }

# robocopy mirror-copy a tree; treat exit codes 0..7 as success (8+ = real failure).
function Copy-Tree($src, $dst, [string]$label) {
    if (-not (Test-Path $src)) { Write-Warning "[package] skip ${label}: source missing -> $src"; return }
    Write-Host "[package] copy $label : $src -> $dst"
    & robocopy $src $dst /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($label) exit=$LASTEXITCODE" }
}

# 1) Base: the offline Extracted tree -> DATA
Copy-Tree (Join-Path $Off 'Extracted') $Data 'Extracted'

# 2) Overlay online (DatasSDO) assets the remake uses. Locate the online client folder by scanning assets/ for the
#    subdir that holds DatasSDO\UI\MUSIC\ICONS (the folder name is oddly encoded, so we don't hardcode it).
$assetsDir = Join-Path $Repo 'assets'
$online = Get-ChildItem -LiteralPath $assetsDir -Directory -ErrorAction SilentlyContinue |
          Where-Object { Test-Path (Join-Path $_.FullName 'DatasSDO\UI\MUSIC\ICONS') } |
          Select-Object -First 1
if ($online) {
    $ds = Join-Path $online.FullName 'DatasSDO'
    Write-Host "[package] online client = $($online.FullName)"
    Copy-Tree (Join-Path $ds 'UI\MUSIC\ICONS')      (Join-Path $Data 'UI\MUSIC\ICONS')      'online ICONS'
    Copy-Tree (Join-Path $ds 'UI\STATIS\STATISTIC') (Join-Path $Data 'UI\STATIS\STATISTIC') 'online STATISTIC'
    # ROOMDLG song-select (選歌) art: overlay the online MUSICSELDLG atlas + .an on top of the offline set so
    # the built player resolves the same 閉撰敃氪 look as the editor (RoomDlgArt's DATA/UI/ROOMDLG fallback).
    Copy-Tree (Join-Path $ds 'UI\ROOMDLG')          (Join-Path $Data 'UI\ROOMDLG')          'online ROOMDLG'
    # OPTION dialog (選項) art: overlay the online OPTIONDLG folder — includes OPTIONDLG.clean.png, the atlas with its
    # baked Chinese painted out by tools\build_optiondlg_clean.py, so the built player resolves the same faithful pink
    # frame the editor does (OptionDlgModal + OptionDlgArt's DATA\UI\OPTIONDLG fallback).
    Copy-Tree (Join-Path $ds 'UI\OPTIONDLG')        (Join-Path $Data 'UI\OPTIONDLG')        'online OPTIONDLG'
    # LOADING screens: the gameplay boot/loading screen (ScreenGameplay boot cover) picks random LOADING_N.PNG tips +
    # LOADINGS_N.PNG badges from here; overlay them so the built player resolves the same set (LoadingArt's DATA\LOADING fallback).
    Copy-Tree (Join-Path $ds 'LOADING')             (Join-Path $Data 'LOADING')             'online LOADING'
} else {
    Write-Warning "[package] online DatasSDO not found under $assetsDir — icons fall back to the offline subset."
}

# 3) Audio + song trees -> DATA (folder names normalized to UPPERCASE)
Copy-Tree (Join-Path $Off 'SE')    (Join-Path $Data 'SE')    'SE'
Copy-Tree (Join-Path $Off 'BGM')   (Join-Path $Data 'BGM')   'BGM'
Copy-Tree (Join-Path $Off 'music') (Join-Path $Data 'MUSIC') 'MUSIC'

# 4) Writable folders: replay saves (under DATA) and screenshots (beside the exe)
New-Item -ItemType Directory -Force -Path (Join-Path $Data 'REPLAY')   | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $BuildDir 'screensave') | Out-Null

# 5) Strip Burst debug-info folders so the top level stays clean
Get-ChildItem -LiteralPath $BuildDir -Directory -Filter '*_BurstDebugInformation_DoNotShip' -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "[package] remove $($_.Name)"; Remove-Item -LiteralPath $_.FullName -Recurse -Force }

Write-Host "[package] done. Top level of $BuildDir :"
Get-ChildItem -LiteralPath $BuildDir | Select-Object Name | Format-Table -HideTableHeaders
