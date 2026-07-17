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
        PROFILE/            <- SEEDED only (existing saves/settings are NEVER overwritten by re-packaging)
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
# -ExcludeDirs: absolute dir paths to skip (robocopy /XD).
function Copy-Tree($src, $dst, [string]$label, [string[]]$ExcludeDirs) {
    if (-not (Test-Path $src)) { Write-Warning "[package] skip ${label}: source missing -> $src"; return }
    Write-Host "[package] copy $label : $src -> $dst"
    $xd = @(); if ($ExcludeDirs) { $xd = @('/XD') + $ExcludeDirs }
    & robocopy $src $dst /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 @xd | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($label) exit=$LASTEXITCODE" }
}

# Copy a tree WITHOUT touching files that already exist at the destination (/XC /XN /XO = only files missing
# there are copied). Used to seed the per-user save tree (PROFILE: profile.json / favorites.json / settings.json /
# active.txt) so re-packaging over an existing build never clobbers live player data. (config.ini is NOT per-user —
# it lives next to the exe and the game writes a commented template there on first boot.)
function Copy-TreeIfMissing($src, $dst, [string]$label) {
    if (-not (Test-Path $src)) { Write-Warning "[package] skip ${label}: source missing -> $src"; return }
    Write-Host "[package] seed $label (existing files kept) : $src -> $dst"
    & robocopy $src $dst /E /XC /XN /XO /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($label) exit=$LASTEXITCODE" }
}

# Decode iteminfo.dat's GBK/CP936 Simplified-Chinese item names into a UTF-8 sidecar (shop_names.tsv, "id<TAB>name"
# per line) so the built player never needs the CJK codepage at runtime: Unity's Mono standalone strips I18N.CJK, so
# Encoding.GetEncoding(936) throws there and names render as mojibake. Windows PowerShell 5.1 HAS CP936, so we decode
# once here at packaging time. Format mirrors Assets/Scripts/Sdo.Shop/IteminfoReader.cs (single source of truth for
# the layout): 12-byte header (int32 headA must be 2), 156-byte records, self-inverse cipher (0x1F9-b)&0xFF, int32 id
# @0x00, GBK name @0x14 (max 44 bytes, NUL-terminated). AvatarItemCatalog.ApplyNameSidecar overlays this at runtime.
function Write-ShopNames($iteminfoPath, $outPath) {
    if (-not (Test-Path $iteminfoPath)) { Write-Warning "[package] shop_names: iteminfo.dat missing -> $iteminfoPath"; return }
    $bytes = [System.IO.File]::ReadAllBytes($iteminfoPath)
    if ($bytes.Length -lt 12 -or [System.BitConverter]::ToInt32($bytes, 0) -ne 2) {
        Write-Warning "[package] shop_names: bad iteminfo header (headA != 2) -> skipped"; return
    }
    $gbk = [System.Text.Encoding]::GetEncoding(936)
    $HeaderLen = 12; $RecordLen = 156; $OffName = 0x14; $NameMax = 44
    $rec = New-Object byte[] $RecordLen
    $sb  = New-Object System.Text.StringBuilder
    $pos = $HeaderLen; $n = 0
    while ($pos + $RecordLen -le $bytes.Length) {
        for ($i = 0; $i -lt $RecordLen; $i++) { $rec[$i] = [byte]((0x1F9 - $bytes[$pos + $i]) -band 0xFF) }
        $id  = [System.BitConverter]::ToInt32($rec, 0)
        $end = $OffName
        while ($end -lt ($OffName + $NameMax) -and $rec[$end] -ne 0) { $end++ }
        $len = $end - $OffName
        if ($len -gt 0) {
            $name = $gbk.GetString($rec, $OffName, $len)
            [void]$sb.Append($id).Append("`t").Append($name).Append("`n")
            $n++
        }
        $pos += $RecordLen
    }
    [System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))  # UTF-8, no BOM
    Write-Host "[package] wrote shop_names.tsv ($n names, UTF-8)"
}

# 1) Base: the offline Extracted tree -> DATA. PROFILE (per-user saves) is excluded from the mirror and
#    SEEDED separately below — a re-package over an existing build must never overwrite the player's live
#    profile.json / favorites.json / settings.json / active.txt. (The game also self-heals: missing PROFILE
#    files are re-created with defaults at boot, see ProfileManager/DisplaySettingsManager. config.ini is
#    global — exe-adjacent, written by RoomConfig on first boot.)
Copy-Tree (Join-Path $Off 'Extracted') $Data 'Extracted' -ExcludeDirs @(Join-Path $Off 'Extracted\PROFILE')
Copy-TreeIfMissing (Join-Path $Off 'Extracted\PROFILE') (Join-Path $Data 'PROFILE') 'PROFILE (seed only)'

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
    # 商城 (SHOP.XML atlas + .an) 與 儲物櫃/更衣間 (MYHOUSEDLG) UI 美術：都是「線上限定」資料夾，離線 Extracted 沒有。
    # ShopArt / CabinetArt 在編輯器從 assets\閉撰敃氪 找、打包則 fallback 到 <exe>\DATA\UI\{SHOP,MYHOUSEDLG}，
    # 沒複製 → 打包後商城/儲物櫃整片黑(素材全 null)。overlay 進 DATA 讓打包版跟編輯器一致。
    Copy-Tree (Join-Path $ds 'UI\SHOP')             (Join-Path $Data 'UI\SHOP')             'online SHOP'
    Copy-Tree (Join-Path $ds 'UI\MYHOUSEDLG')       (Join-Path $Data 'UI\MYHOUSEDLG')       'online MYHOUSEDLG'
    # OPTION 鍵盤 tab per-key letter glyphs (A/S/W/D…, blue-fill/white-outline PNGs blitted on each key cap; loaded by
    # KeysArt with a DATA\UI\LOBBYDLG\KEYS fallback). Not referenced by any .an — the exe loaded them by hardcoded path.
    Copy-Tree (Join-Path $ds 'UI\LOBBYDLG\KEYS')    (Join-Path $Data 'UI\LOBBYDLG\KEYS')    'online KEYS glyphs'
    # LOADING screens: the gameplay boot/loading screen (ScreenGameplay boot cover) picks random LOADING_N.PNG tips +
    # LOADINGS_N.PNG badges from here; overlay them so the built player resolves the same set (LoadingArt's DATA\LOADING fallback).
    Copy-Tree (Join-Path $ds 'LOADING')             (Join-Path $Data 'LOADING')             'online LOADING'
    # 商城目錄：iteminfo.dat (單品名/價) + setinfo.dat (套装組件) 放到 DATA 根,讓打包後 AvatarItemCatalog 找得到 (編輯器
    # 從 assets/閉撰敃氪 找,打包則從 <exe>/DATA 找)。兩檔在線上客戶端根目錄 (閉撰敃氪/),不在 DatasSDO 下。
    foreach ($f in @('iteminfo.dat','setinfo.dat')) {
        $src = Join-Path $online.FullName $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $Data $f) -Force; Write-Host "[package] copied $f" }
        else { Write-Warning "[package] $f not found at $src" }
    }
    # Bake the UTF-8 name sidecar from the iteminfo.dat we just staged, so ids match exactly what the runtime reads.
    Write-ShopNames (Join-Path $Data 'iteminfo.dat') (Join-Path $Data 'shop_names.tsv')
} else {
    Write-Warning "[package] online DatasSDO not found under $assetsDir — icons fall back to the offline subset."
}

# 2b) Traditional-Chinese (TW 櫻式搖滾) name overlay: shop_names_tw.tsv (category<TAB>modelId<TAB>Big5-decoded-name).
# Committed at tools\data\ (produced by tools\build_shop_names_tw.py from the TW iteminfo.dat, a different 152-byte/Big5
# format the runtime reader ignores). AvatarItemCatalog overlays it to fill unnamed mesh-only rows + swap CN Simplified
# names for the official Traditional ones. Independent of the online overlay above, so copied here unconditionally.
$twNames = Join-Path $Repo 'tools\data\shop_names_tw.tsv'
if (Test-Path $twNames) {
    Copy-Item $twNames (Join-Path $Data 'shop_names_tw.tsv') -Force
    Write-Host "[package] copied shop_names_tw.tsv (繁體 name overlay)"
} else {
    Write-Warning "[package] shop_names_tw.tsv not found at $twNames — built shop keeps 簡體/序號 names (run tools\build_shop_names_tw.py)"
}
# 台版官方套装 (古惑仔/卡卡西/逍遙英雄…): AvatarItemCatalog.AddTwSets 讀它,加進 套装 分頁。
$twSets = Join-Path $Repo 'tools\data\shop_sets_tw.tsv'
if (Test-Path $twSets) {
    Copy-Item $twSets (Join-Path $Data 'shop_sets_tw.tsv') -Force
    Write-Host "[package] copied shop_sets_tw.tsv (繁體 套装)"
} else {
    Write-Warning "[package] shop_sets_tw.tsv not found at $twSets — built shop has no 台版套装 (run tools\build_shop_names_tw.py)"
}

# 3) Audio + song trees -> DATA (folder names normalized to UPPERCASE)
Copy-Tree (Join-Path $Off 'SE')    (Join-Path $Data 'SE')    'SE'
# BGM: the lobby/room random playlist lives in Extracted/UI/BGM (bgm_000..007.ogg) — ship it at DATA/BGM (UiBgmDir's
# preferred location) and drop the copies older layouts left at DATA/UI/BGM (Extracted mirror) and DATA/BGA (a
# short-lived rename). The old top-level sdox_offline/BGM (BMG_/TEACHING) has NO consumer in the remake, so it is
# no longer shipped — DATA/BGM holds the lobby tracks.
Copy-Tree (Join-Path $Off 'Extracted\UI\BGM') (Join-Path $Data 'BGM') 'BGM (lobby, from Extracted/UI/BGM)'
foreach ($stale in @((Join-Path $Data 'UI\BGM'), (Join-Path $Data 'BGA'))) {
    if (Test-Path $stale) { Remove-Item -LiteralPath $stale -Recurse -Force; Write-Host "[package] removed $stale (lobby bgm now at DATA\BGM)" }
}
Copy-Tree (Join-Path $Off 'music') (Join-Path $Data 'MUSIC') 'MUSIC'

# 4) Writable folders: replay saves (under DATA) and screenshots (beside the exe)
New-Item -ItemType Directory -Force -Path (Join-Path $Data 'REPLAY')   | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $BuildDir 'screensave') | Out-Null

# 5) Strip Burst debug-info folders so the top level stays clean
Get-ChildItem -LiteralPath $BuildDir -Directory -Filter '*_BurstDebugInformation_DoNotShip' -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "[package] remove $($_.Name)"; Remove-Item -LiteralPath $_.FullName -Recurse -Force }

Write-Host "[package] done. Top level of $BuildDir :"
Get-ChildItem -LiteralPath $BuildDir | Select-Object Name | Format-Table -HideTableHeaders
# robocopy leaves 1/2 ("copied"/"extras at destination") in $LASTEXITCODE on success; exit 0 so callers don't see a false failure.
exit 0
