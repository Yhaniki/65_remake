<#
.SYNOPSIS
  Assemble a CLEAN, ship-structured DATA/ folder containing only assets the remake actually uses.
  "Safe version": full song + full costume catalogs kept; dead files pruned ONLY where statically provable
  (SE by name-reference scan; sdox_offline/BGM dropped as it has no consumer). Content folders
  (MOTION/UI/SCENE/CAMERA/DANCE/AUMOTION/3DEFT/EFFECT/NOTEIMAGE) are kept whole — pruning those safely
  needs a runtime trace (see docs/USED_ASSETS.md).

.DESCRIPTION
  Output layout = what the built player expects under DATA/ (mirrors tools/package_build.ps1) PLUS the full
  Datas/AVATAR costume catalog merged into DATA/AVATAR (the stock ship build ships only the 120 base meshes;
  the user asked to keep all costumes). Sources are read-only; originals under assets/ are never modified.

    DATA/
      <Extracted whole>            base: MOTION SCENE CAMERA DANCE 3DEFT EFFECT NOTEIMAGE AUMOTION 3DNOTES
                                   UI(+UI/BGM used) AVATAR(120 base) LOADING DRESS.TXT PROIFO.* ...
      PROFILE/   <- SEEDED only (existing saves/settings at the destination are never overwritten)
      AVATAR/    <- + Datas/AVATAR (full 38k costume mesh+texture catalog)
      UI/...     <- online overlays (ICONS, STATIS/STATISTIC, ROOMDLG, OPTIONDLG, SHOP, MYHOUSEDLG, LOBBYDLG/KEYS)
      LOADING/   <- online overlay
      MUSIC/     <- full song tree
      SE/        <- ONLY the .wav referenced by code (dead ones like bingo.wav dropped)
      BGM/       <- lobby/room random playlist (from Extracted/UI/BGM; the dead sdox_offline/BGM set is NOT shipped)
      iteminfo.dat setinfo.dat shop_names.tsv  REPLAY/
      CLEAN_MANIFEST.txt   <- what was kept / dropped per subsystem

.PARAMETER Dest   Parent folder; DATA/ is created inside. Default H:\65_remake_clean.
.PARAMETER DryRun Report the plan (incl. SE keep/drop) without copying.
#>
[CmdletBinding()]
param(
    [string]$Dest = 'H:\65_remake_clean',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$Repo    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Assets  = Join-Path $Repo 'assets'
$Off     = Join-Path $Assets 'sdox_offline'
$Extract = Join-Path $Off 'Extracted'
$Scripts = Join-Path $Repo '65\My project\Assets\Scripts'
$Data    = Join-Path $Dest 'DATA'

# locate online client dir (holds DatasSDO) by content, not by its odd name
$online = Get-ChildItem -LiteralPath $Assets -Directory -ErrorAction SilentlyContinue |
          Where-Object { Test-Path (Join-Path $_.FullName 'DatasSDO\UI\MUSIC\ICONS') } | Select-Object -First 1
if (-not $online) { throw "online DatasSDO folder not found under $Assets" }
$OnlineDir = $online.FullName
$Ds = Join-Path $OnlineDir 'DatasSDO'

Write-Host "[clean] repo   = $Repo"
Write-Host "[clean] online = $($online.Name)"
Write-Host "[clean] dest   = $Data"
Write-Host "[clean] mode   = $(if ($DryRun) {'DRY-RUN'} else {'BUILD'})"
Write-Host ''

# ---- derive the USED SE set: for each SE/*.wav stem, is it referenced anywhere in the C# source? ----
$srcText = (Get-ChildItem -LiteralPath $Scripts -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
            ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n"
$SeSrc = Join-Path $Off 'SE'
$seKeep = New-Object System.Collections.Generic.List[string]
$seDrop = New-Object System.Collections.Generic.List[string]
foreach ($w in Get-ChildItem -LiteralPath $SeSrc -Filter *.wav -File -ErrorAction SilentlyContinue) {
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($w.Name)
    if ($srcText -match ('(?i)\b' + [regex]::Escape($stem) + '\b')) { $seKeep.Add($w.Name) } else { $seDrop.Add($w.Name) }
}
Write-Host ("[clean] SE: {0} referenced (keep), {1} dead (drop)" -f $seKeep.Count, $seDrop.Count)
Write-Host ("[clean] SE dropped: {0}" -f (($seDrop | ForEach-Object { $_ -replace '\.wav$','' }) -join ', '))
Write-Host ''

if ($DryRun) { Write-Host "[clean] dry-run only — re-run without -DryRun to build."; return }

# ---- helpers ----
# -ExcludeDirs: absolute dir paths to skip (robocopy /XD).
function Copy-Tree($src, $dst, $label, [string[]]$ExcludeDirs) {
    if (-not (Test-Path $src)) { Write-Warning "[clean] skip ${label}: missing $src"; return }
    Write-Host "[clean] copy $label"
    $xd = @(); if ($ExcludeDirs) { $xd = @('/XD') + $ExcludeDirs }
    & robocopy $src $dst /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 @xd | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($label) exit=$LASTEXITCODE" }
}
# Copy only files MISSING at the destination (/XC /XN /XO — existing files are never overwritten). Used to seed
# PROFILE so re-running over a destination that has live saves/settings never clobbers them.
function Copy-TreeIfMissing($src, $dst, $label) {
    if (-not (Test-Path $src)) { Write-Warning "[clean] skip ${label}: missing $src"; return }
    Write-Host "[clean] seed $label (existing files kept)"
    & robocopy $src $dst /E /XC /XN /XO /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($label) exit=$LASTEXITCODE" }
}
# GBK iteminfo.dat -> UTF-8 shop_names.tsv (identical to tools/package_build.ps1)
function Write-ShopNames($iteminfoPath, $outPath) {
    if (-not (Test-Path $iteminfoPath)) { Write-Warning "[clean] shop_names: missing $iteminfoPath"; return }
    $bytes = [System.IO.File]::ReadAllBytes($iteminfoPath)
    if ($bytes.Length -lt 12 -or [System.BitConverter]::ToInt32($bytes,0) -ne 2) { Write-Warning "[clean] shop_names: bad header"; return }
    $gbk = [System.Text.Encoding]::GetEncoding(936); $HeaderLen=12; $RecordLen=156; $OffName=0x14; $NameMax=44
    $rec = New-Object byte[] $RecordLen; $sb = New-Object System.Text.StringBuilder; $pos=$HeaderLen; $n=0
    while ($pos + $RecordLen -le $bytes.Length) {
        for ($i=0; $i -lt $RecordLen; $i++) { $rec[$i] = [byte]((0x1F9 - $bytes[$pos+$i]) -band 0xFF) }
        $id = [System.BitConverter]::ToInt32($rec,0); $end=$OffName
        while ($end -lt ($OffName+$NameMax) -and $rec[$end] -ne 0) { $end++ }
        if (($end-$OffName) -gt 0) { [void]$sb.Append($id).Append("`t").Append($gbk.GetString($rec,$OffName,$end-$OffName)).Append("`n"); $n++ }
        $pos += $RecordLen
    }
    [System.IO.File]::WriteAllText($outPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "[clean] wrote shop_names.tsv ($n names)"
    Convert-ShopNamesToTraditional $outPath '[clean]'
}
# 簡體 → 台灣正體 (OpenCC s2twp), 與 tools\package_build.ps1 相同: 讓 editor 讀到的 clean shop_names.tsv 也是繁體。
# 需要 python + opencc; 缺任一則保留簡體、僅警告, 不中斷。轉換邏輯唯一來源 = tools\convert_shop_names_s2t.py。
function Convert-ShopNamesToTraditional($tsvPath, $tag) {
    $conv = Join-Path $PSScriptRoot 'convert_shop_names_s2t.py'
    if (-not (Test-Path $conv)) { Write-Warning "$tag convert_shop_names_s2t.py 不存在 — shop_names 保留簡體"; return }
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { Write-Warning "$tag 無 python — shop_names 保留簡體"; return }
    try {
        & python -X utf8 $conv $tsvPath
        if ($LASTEXITCODE -ne 0) { Write-Warning "$tag shop_names 簡→繁 轉換失敗 (缺 opencc?) — 保留簡體" }
    } catch { Write-Warning "$tag shop_names 簡→繁 轉換例外: $($_.Exception.Message) — 保留簡體" }
}

New-Item -ItemType Directory -Force -Path $Data | Out-Null

# 1) base = Extracted whole (content folders kept whole: MOTION/SCENE/CAMERA/DANCE/3DEFT/EFFECT/NOTEIMAGE/UI/AVATAR/...)
#    PROFILE (per-user saves/settings) is excluded from the mirror and seeded separately so re-running over a
#    destination that already has live saves never overwrites them.
Copy-Tree $Extract $Data 'Extracted (base)' -ExcludeDirs @(Join-Path $Extract 'PROFILE')
Copy-TreeIfMissing (Join-Path $Extract 'PROFILE') (Join-Path $Data 'PROFILE') 'PROFILE (seed only)'

# 1b) BGM: pull the lobby/room random playlist (bgm_000..007.ogg) OUT of UI to the DATA root (DATA/BGM = UiBgmDir's
#     preferred location); drop the copies older layouts left at DATA/UI/BGM and DATA/BGA (a short-lived rename).
#     (The dead top-level sdox_offline/BGM BMG_/TEACHING set is never brought in — see manifest.)
Copy-Tree (Join-Path $Extract 'UI\BGM') (Join-Path $Data 'BGM') 'BGM (lobby bgm -> DATA/BGM)'
foreach ($stale in @((Join-Path $Data 'UI\BGM'), (Join-Path $Data 'BGA'))) {
    if (Test-Path $stale) { Remove-Item -LiteralPath $stale -Recurse -Force; Write-Host "[clean] removed $stale (lobby bgm now at DATA\BGM)" }
}

# 2) merge full costume catalog Datas/AVATAR -> DATA/AVATAR
Copy-Tree (Join-Path $Assets 'Datas\AVATAR') (Join-Path $Data 'AVATAR') 'Datas/AVATAR (full costume catalog)'

# 3) online UI overlays — EXACTLY package_build's set (BUBBLE2/EXPRESSIONS come from the Extracted base, matching ship)
Copy-Tree (Join-Path $Ds 'UI\MUSIC\ICONS')      (Join-Path $Data 'UI\MUSIC\ICONS')      'online ICONS'
Copy-Tree (Join-Path $Ds 'UI\STATIS\STATISTIC') (Join-Path $Data 'UI\STATIS\STATISTIC') 'online STATISTIC'
Copy-Tree (Join-Path $Ds 'UI\ROOMDLG')          (Join-Path $Data 'UI\ROOMDLG')          'online ROOMDLG'
Copy-Tree (Join-Path $Ds 'UI\OPTIONDLG')        (Join-Path $Data 'UI\OPTIONDLG')        'online OPTIONDLG'
Copy-Tree (Join-Path $Ds 'UI\SHOP')             (Join-Path $Data 'UI\SHOP')             'online SHOP'
Copy-Tree (Join-Path $Ds 'UI\MYHOUSEDLG')       (Join-Path $Data 'UI\MYHOUSEDLG')       'online MYHOUSEDLG'
Copy-Tree (Join-Path $Ds 'UI\LOBBYDLG\KEYS')    (Join-Path $Data 'UI\LOBBYDLG\KEYS')    'online KEYS'
Copy-Tree (Join-Path $Ds 'LOADING')             (Join-Path $Data 'LOADING')             'online LOADING'

# 4) shop item data
foreach ($f in 'iteminfo.dat','setinfo.dat') {
    $src = Join-Path $OnlineDir $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $Data $f) -Force; Write-Host "[clean] copied $f" }
}
Write-ShopNames (Join-Path $Data 'iteminfo.dat') (Join-Path $Data 'shop_names.tsv')

# 5) MUSIC (full song tree)
Copy-Tree (Join-Path $Off 'music') (Join-Path $Data 'MUSIC') 'MUSIC (full)'

# 6) SE — copy ONLY referenced .wav (drops bingo.wav etc.)
$SeDst = Join-Path $Data 'SE'; New-Item -ItemType Directory -Force -Path $SeDst | Out-Null
foreach ($name in $seKeep) { Copy-Item (Join-Path $SeSrc $name) (Join-Path $SeDst $name) -Force }
Write-Host ("[clean] SE: copied {0} referenced wav (skipped {1} dead)" -f $seKeep.Count, $seDrop.Count)

# 7) writable folder
New-Item -ItemType Directory -Force -Path (Join-Path $Data 'REPLAY') | Out-Null

# ---- manifest ----
$mf = New-Object System.Text.StringBuilder
[void]$mf.AppendLine("CLEAN DATA manifest — safe version (content folders kept whole)")
[void]$mf.AppendLine("Generated from assets/ by tools/build_clean_data.ps1")
[void]$mf.AppendLine("")
[void]$mf.AppendLine("KEPT WHOLE (content, reachable ~= all with full song/costume catalogs):")
[void]$mf.AppendLine("  MOTION AUMOTION DANCE SCENE CAMERA 3DEFT EFFECT NOTEIMAGE 3DNOTES  (from Extracted)")
[void]$mf.AppendLine("  UI/* (Extracted) + online overlays ICONS/STATISTIC/ROOMDLG/OPTIONDLG/SHOP/MYHOUSEDLG/KEYS")
[void]$mf.AppendLine("  BGM (8 ogg, lobby/room random playlist) — MOVED out of UI/BGM to the DATA root (DATA/BGM)")
[void]$mf.AppendLine("  AVATAR = Extracted(120) + Datas/AVATAR(full 38k costume catalog)")
[void]$mf.AppendLine("  MUSIC = full song tree")
[void]$mf.AppendLine("")
[void]$mf.AppendLine("PRUNED (statically provable dead):")
[void]$mf.AppendLine(("  SE: kept {0} referenced, dropped {1} unreferenced:" -f $seKeep.Count, $seDrop.Count))
[void]$mf.AppendLine("      " + (($seDrop | ForEach-Object { $_ -replace '\.wav$','' }) -join ' '))
[void]$mf.AppendLine("  BGM: sdox_offline/BGM (BMG_/TEACHING, 31 files) DROPPED — no PlayBgm consumer in the remake.")
[void]$mf.AppendLine("")
[void]$mf.AppendLine("NOT YET PRUNED (needs runtime trace to do safely): content folders above may still")
[void]$mf.AppendLine("contain files no current code path reaches (e.g. unused MOTION/UI atlases).")
[System.IO.File]::WriteAllText((Join-Path $Data 'CLEAN_MANIFEST.txt'), $mf.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "[clean] done -> $Data"
exit 0
