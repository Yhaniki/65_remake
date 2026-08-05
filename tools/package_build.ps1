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
        MODEL/<name>/*.pmx  <- MMD models (from assets\MODEL); one folder per model, picked in the 設定 panel
        UI/MUSIC/ICONS      <- overlaid with the FULL online (DatasSDO) icon set
        UI/STATIS/STATISTIC <- overlaid with the online result-screen art (safety; usually already in Extracted)

  All source paths derive from the repo root ($PSScriptRoot\..) — no hardcoded drive letters.

.PARAMETER BuildDir
  The build output folder containing the exe. Default: <repo>\Build\Windows.
#>
[CmdletBinding()]
param(
    [string]$BuildDir,

    # 組完 DATA 之後把它打包成 SDOPAK 分卷（tools\build_pak.py），並刪掉散裝樹。
    # 預設關 —— 開發時散裝樹好查、改一個檔就生效，不必重打包。
    [switch]$Pack,

    # 打包時加密（出貨用）。⚠️ 混淆不是保護：金鑰必然在執行檔裡。
    # 見 docs\architecture\data-packaging.md §5。
    [switch]$Encrypt
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
# there are copied). Used to seed the save tree (PROFILE: per-user profile.json + the global favorites.json /
# config.ini / keymaps.ini) so re-packaging over an existing build never clobbers live player data. (config.ini and
# keymaps.ini are global-not-per-user but live in DATA/PROFILE; the game writes commented templates there on first
# boot, so shipping them is optional.)
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
    Convert-ShopNamesToTraditional $outPath '[package]'
}

# 簡體 → 台灣正體 (OpenCC s2twp): iteminfo.dat 名稱是大陸簡體, 在此轉成台版繁體字/用詞, 讓遊戲內不再出現簡體。
# 官方 635 筆道地台版名仍由 shop_names_tw.tsv 最後蓋頂 (AvatarItemCatalog.ApplyTwNames), 這裡只處理其餘簡體那批。
# 需要 python + opencc; 缺任一則保留簡體、僅警告, 絕不中斷 build。轉換邏輯的唯一來源 = tools\convert_shop_names_s2t.py。
function Convert-ShopNamesToTraditional($tsvPath, $tag) {
    $conv = Join-Path $PSScriptRoot 'convert_shop_names_s2t.py'
    if (-not (Test-Path $conv)) { Write-Warning "$tag convert_shop_names_s2t.py 不存在 — shop_names 保留簡體"; return }
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Write-Warning "$tag 無 python — shop_names 保留簡體"; return }
    try {
        & python -X utf8 $conv $tsvPath
        if ($LASTEXITCODE -ne 0) { Write-Warning "$tag shop_names 簡→繁 轉換失敗 (缺 opencc?) — 保留簡體" }
    } catch { Write-Warning "$tag shop_names 簡→繁 轉換例外: $($_.Exception.Message) — 保留簡體" }
}

# 1) Base: the offline Extracted tree -> DATA. PROFILE (per-user saves) is excluded from the mirror and
#    SEEDED separately below — a re-package over an existing build must never overwrite the player's live
#    profile.json / favorites.json / config.ini / keymaps.ini. (The game also self-heals: missing PROFILE files are
#    re-created with defaults at boot, see ProfileManager/RoomConfig/ProfileDefaults/KeyMap. config.ini (settings),
#    profile.json (active character + family/level defaults) and keymaps.ini (key bindings) are global-not-per-user
#    but live in DATA/PROFILE, written on first boot.)
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

# 2c) Upscaled art overlay: art\upscaled mirrors the DATA layout and carries higher-resolution replacements for art
# whose shipped resolution is too low for today's screens:
#   UI\PLAYINGEXP 表情 cut-in    64px 原圖 -> 192px hq3x  (tools\upscale_playingexp.py)
#   3DEFT\GENERIC\MAP_G\KEKKAI  512px    -> 2048px       (tools\upscale_kekkai.py) — SCN0008 地板結界
# …以及少數「原檔內容寫錯」的修正檔(同樣是取代既有檔,所以放這棵樹):
#   UI\ROOM\ROOM93.AN                       「進入」鈕 hover 幀指錯格
#   NOTEIMAGE\NOTEIMAGE_8\NOTEIMAGE(_MOVEDOWN).AN  長條尾帽槽位被填成 note 頭 → 尾帽整個不畫 (見 build_clean_data.ps1)
# Copied ON TOP of the Extracted base, so only the named files are replaced.
# The on-screen SIZE is unchanged — the loaders divide it back out (SdoExtracted.LoadImageAtDesignWidth, guarded by
# EmojiUpscaleTests); the EFT texture is sampled over the full UV, so its resolution is free.
# Never mirror/mirror-delete here: the folders it lands in also hold art we must not touch.
$upscaled = Join-Path $Repo 'art\upscaled'
if (Test-Path $upscaled) {
    Copy-Tree $upscaled $Data 'upscaled art overlay'
} else {
    Write-Warning "[package] art\upscaled not found — 表情 cut-in 維持 64px, KEKKAI 維持 512px (run tools\upscale_playingexp.py / upscale_kekkai.py)"
}

# 2d) Generated art overlay: art\generated carries art the original never had, baked in the original's style from
# official plates:
#   UI\LOBBYSEL\LOBBYSEL200..205 = 開房/加入 三態鈕 (tools\make_lobbysel_room_buttons.py)
#   UI\ROOM\{C06..C09,D06..D09}  = 頭貼徽章條的 NO MAP / PLAYING 四色幀 (延續官方 READY=a06.. / HOST=b06.. 的編號;
#                                  原版 Extracted 沒有這幾張,靠這棵樹帶進出貨包 —— 見 RoomStateBadgeArtTests)
# A separate tree from art\upscaled on purpose: 'upscaled' means "replaces a shipped file", 'generated' means
# "brand-new filename". Same mirror-the-DATA-layout + copy-on-top rule, so nothing existing is touched.
$generated = Join-Path $Repo 'art\generated'
if (Test-Path $generated) {
    Copy-Tree $generated $Data 'generated art overlay'
} else {
    Write-Warning "[package] art\generated not found — 連線用的新按鈕與頭貼的 NO MAP / PLAYING 徽章會缺圖"
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

# 3b) MMD models -> DATA/MODEL. One sub-folder per model, each holding its .pmx plus its own textures/Toon/Sph — the
#     same layout the editor reads from assets\MODEL, so a model that works in play mode works in the built player.
#     Without this the packaged game finds no model and the MMD swap (F7) silently stays on the SDO body.
Copy-Tree (Join-Path $assetsDir 'MODEL') (Join-Path $Data 'MODEL') 'MMD models'
# The original single-model layout (assets\IkaHatunemiku2025, before DATA\MODEL existed) still ships, as DATA\MODEL\<its name>.
$mmdLegacy = Join-Path $assetsDir 'IkaHatunemiku2025'
if (Test-Path $mmdLegacy) { Copy-Tree $mmdLegacy (Join-Path $Data 'MODEL\IkaHatunemiku2025') 'MMD model (legacy folder)' }

# 4) Writable folders: replay saves (under DATA) and screenshots (beside the exe)
New-Item -ItemType Directory -Force -Path (Join-Path $Data 'REPLAY')   | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $BuildDir 'screensave') | Out-Null

# 4b) ADDON plugin tree — empty folders the player drops into: SONG (osu/StepMania songs, scanned at boot),
#     plus reserved NOTESKIN / THEME / MODEL for future plugin loaders. Runtime also creates these on first launch
#     (SdoExtracted.EnsureAddonDirs); shipping them means a fresh build already shows where things go.
foreach ($sub in 'SONG','NOTESKIN','THEME','MODEL') {
    New-Item -ItemType Directory -Force -Path (Join-Path $Data (Join-Path 'ADDON' $sub)) | Out-Null
}

# 5) Strip Burst debug-info folders so the top level stays clean
Get-ChildItem -LiteralPath $BuildDir -Directory -Filter '*_BurstDebugInformation_DoNotShip' -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "[package] remove $($_.Name)"; Remove-Item -LiteralPath $_.FullName -Recurse -Force }

# 6) 打包成 SDOPAK 分卷（-Pack）。
#
#    順序是刻意的：先把散裝 DATA 完整組好（上面所有的 overlay / 覆蓋 / 產生的美術都套完），
#    再一次打包 —— 打包器看到的就是最終狀態，不必知道任何組裝規則。
#
#    reserved 目錄（PROFILE / ADDON / CACHE / REPLAY）留在原地不動：那是玩家可寫的明碼區，
#    打包器自己也會擋。刪散裝樹時同樣跳過它們。
if ($Pack) {
    $pyPack = Join-Path $PSScriptRoot 'build_pak.py'
    if (-not (Test-Path $pyPack)) { throw "找不到 $pyPack" }

    # manifest 寫到 build 目錄旁邊，**不進 DATA** —— 它是每一條路徑的明文（base_avatar 那份 5.4 MB），
    # 跟著出貨等於索引加密白做。留在 build 目錄是因為下次產 patch 卷要拿它比對。
    $ManifestDir = Join-Path $BuildDir 'pak_manifests'

    Write-Host "[package] pack: $Data -> SDOPAK$(if ($Encrypt) { '（加密）' } else { '（明碼）' })"
    $packArgs = @($pyPack, '--source', $Data, '--out', $Data, '--manifest-dir', $ManifestDir)
    if ($Encrypt) { $packArgs += '--encrypt' }
    & python @packArgs
    if ($LASTEXITCODE -ne 0) { throw "build_pak.py 失敗 exit=$LASTEXITCODE" }

    # 打包成功才刪散裝樹 —— 失敗時留著，至少 build 還是能跑的。
    #
    # 🔴 **只刪 packed_dirs.json 說有打包的那些**。不能反過來寫成「除了 PROFILE/ADDON/… 以外全刪」——
    #    音訊（BGM / SE / MUSIC）目前刻意維持散裝（見 build_pak.py 的 VOLUMES: loose=True），
    #    那種寫法會把它們一起刪掉，症狀是「遊戲完全沒有聲音而且不報錯」。
    #    清單由打包器產出，兩邊才不會各自維護一份而漂移。
    $packedJson = Join-Path $ManifestDir 'packed_dirs.json'
    if (-not (Test-Path $packedJson)) { throw "build_pak.py 沒有產生 packed_dirs.json —— 不敢刪散裝樹" }
    $packed = (Get-Content $packedJson -Raw -Encoding UTF8 | ConvertFrom-Json).packed

    Get-ChildItem -LiteralPath $Data -Directory | Where-Object { $packed -contains $_.Name } | ForEach-Object {
        Write-Host "[package] pack: 移除散裝 $($_.Name)"
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
    # 頂層的零星檔案（iteminfo.dat / shop_names.tsv…）已經進了 base_core.pak。出貨的 DATA 只留 *.pak。
    Get-ChildItem -LiteralPath $Data -File | Where-Object { $_.Extension -ne '.pak' } | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
    }

    Write-Host "[package] pack: DATA 現在是"
    Get-ChildItem -LiteralPath $Data | Select-Object Name, @{n='MB';e={ if ($_.PSIsContainer) { '' } else { '{0:N1}' -f ($_.Length/1MB) } }} | Format-Table -AutoSize
}

Write-Host "[package] done. Top level of $BuildDir :"
Get-ChildItem -LiteralPath $BuildDir | Select-Object Name | Format-Table -HideTableHeaders
# robocopy leaves 1/2 ("copied"/"extras at destination") in $LASTEXITCODE on success; exit 0 so callers don't see a false failure.
exit 0
