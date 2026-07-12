<#
.SYNOPSIS
  Prune dead files from a ship-structured DATA/ folder using a RUNTIME USED-SET (the list of files the
  UsedAssetsProbe actually opened — see tools/run_probe_trace.ps1 + Assets/Scripts/Game/UsedAssetsProbe.cs).
  SAFE BY DESIGN: default is a DRY-RUN report; -Execute MOVES (never deletes) candidates into a quarantine
  folder so a smoke-test can prove nothing broke, and -Restore puts them all back.

.DESCRIPTION
  A file under -Data is a DELETION CANDIDATE iff it is BOTH:
    * NOT in the used-set (never opened by the probe across the full enumeration), AND
    * NOT matched by a keep-glob (small config/index/writable files opened lazily or written at runtime,
      which a load-only trace may legitimately never touch).
  Everything else is kept. This errs toward KEEPING files: a file the probe merely *probed* for (File.Exists
  that succeeded) counts as used, so we only ever drop files that no code path referenced at all.

  The used-set file (-Used) is one path per line, either absolute (under -UsedRoot) or already DATA-relative;
  both are normalised to lower-case backslash DATA-relative before comparison (NTFS is case-insensitive).

.PARAMETER Data       The DATA/ folder to prune (the tree being shipped).           Default H:\65_remake_clean\DATA
.PARAMETER Used       Text file: the used-set (from the procmon trace).             Required unless -Restore.
.PARAMETER UsedRoot   Prefix to strip from absolute lines in -Used to get a DATA-relative path. Default = -Data.
.PARAMETER Quarantine Folder candidates are MOVED into (mirrors DATA layout).       Default <Data>\..\DATA_quarantine
.PARAMETER KeepGlobs  Extra always-keep -like patterns (matched against DATA-relative path, lower-case).
.PARAMETER Execute    Actually move candidates into the quarantine. Without it: report only.
.PARAMETER Restore    Move everything in the quarantine back into DATA (undo a previous -Execute) and exit.
#>
[CmdletBinding()]
param(
    [string]$Data = 'H:\65_remake_clean\DATA',
    [string]$Used,
    [string]$UsedRoot,
    [string]$Quarantine,
    [string[]]$KeepGlobs = @(),
    [switch]$Execute,
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'
$Data = (Resolve-Path -LiteralPath $Data).Path.TrimEnd('\')
if (-not $Quarantine) { $Quarantine = Join-Path (Split-Path $Data -Parent) 'DATA_quarantine' }

function Rel($full, $root) {
    $r = $full
    if ($full.ToLowerInvariant().StartsWith(($root.ToLowerInvariant() + '\'))) { $r = $full.Substring($root.Length + 1) }
    return $r.TrimStart('\').ToLowerInvariant() -replace '/', '\'
}

# ---- Restore mode: undo a prior quarantine, then exit ----
if ($Restore) {
    if (-not (Test-Path -LiteralPath $Quarantine)) { throw "nothing to restore: $Quarantine not found" }
    $n = 0
    Get-ChildItem -LiteralPath $Quarantine -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($Quarantine.Length).TrimStart('\')
        $dst = Join-Path $Data $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
        Move-Item -LiteralPath $_.FullName -Destination $dst -Force
        $n++
    }
    Write-Host "[prune] restored $n file(s) from $Quarantine -> $Data"
    exit 0
}

if (-not $Used) { throw "-Used <used-set file> is required (or pass -Restore)" }
if (-not (Test-Path -LiteralPath $Used)) { throw "used-set file not found: $Used" }
if (-not $UsedRoot) { $UsedRoot = $Data } else { $UsedRoot = $UsedRoot.TrimEnd('\') }

# ---- always-keep: small config / index / writable files a load-only trace may not open ----
# (Content files — MSH/MOT/DDS/GN — are governed by the trace, NOT kept here.)
$defaultKeep = @(
    'profile\*', 'replay\*',              # per-user writable + replay output
    '*.ini', 'active.txt',                # config
    '*.json',                             # runtime catalogs / overrides (song_name_overrides, etc.)
    'shop_names.tsv', 'clean_manifest.txt',
    'iteminfo.dat', 'setinfo.dat',        # shop data (read once at catalog load; keep regardless)
    'dress.txt', 'proifo*',               # default-outfit + profile index tables
    'avatar\female.hrc', 'avatar\male.hrc', # base skeletons (belt-and-suspenders; probe should touch these)
    'bgm\*'                                 # front-end lobby bgm: DATA/BGM is its ship home (UiBgmDir reads BGM first,
                                            # then UI/BGM). Real assets a load-only trace may not open before the first
                                            # track plays — always keep.
)
$keep = @($defaultKeep + $KeepGlobs)
function IsKept($rel) { foreach ($g in $keep) { if ($rel -like $g) { return $true } } return $false }

# FORCE-DEAD: files the coarse trace "touched" but the game can NEVER load. Overrides the used-set.
#  • UI 2D art is loaded as .an/.png/.bmp only (SdoExtracted.LoadTexture can't decode DDS; no UI code uses DdsLoader),
#    so the original .DDS atlases (+ extraction junk) under ui\ are redundant.
#  • MUSIC: the browse UI curates each sdomNNNNk/t.gn pair down to the 'k' chart (SongListModel.Curate), and LoadChart
#    is the only gn-FILE reader (always a k path). So every 't' + short-tutorial gn FILE, and any top-level ogg with no
#    'k' chart, is never loaded (their catalog ENTRIES still exist for title/artist lookups + font warmup — strings only).
$forceDeadUiExt = @('.dds', '.dds_old', '.png_old', '.rar', '.exe', '.db', '.bat', '.bak')
# valid audio bases = the 'k' charts present in MUSIC (only these are ever played)
$validOggBase = New-Object 'System.Collections.Generic.HashSet[string]'
$musicDir = Join-Path $Data 'MUSIC'
if (Test-Path -LiteralPath $musicDir) {
    foreach ($g in [System.IO.Directory]::EnumerateFiles($musicDir, '*.gn')) {
        $bn = [System.IO.Path]::GetFileNameWithoutExtension($g).ToLowerInvariant()
        if ($bn.EndsWith('k')) { $mm = [regex]::Match($bn, 'sdom\d+'); if ($mm.Success) { [void]$validOggBase.Add($mm.Value) } }
    }
}
function IsForceDead($rel) {
    $ext = [System.IO.Path]::GetExtension($rel).ToLowerInvariant()
    if ($rel -like 'ui\*' -and ($forceDeadUiExt -contains $ext)) { return $true }
    if ($rel -like 'music\*') {
        $bn = [System.IO.Path]::GetFileNameWithoutExtension($rel).ToLowerInvariant()
        if ($ext -eq '.gn' -and -not $bn.EndsWith('k')) { return $true }              # non-primary chart file: never loaded
        if ($ext -eq '.ogg' -and ($rel -match '^music\\[^\\]+\.ogg$')) {              # top-level ogg (NOT exper\ previews)
            $mm = [regex]::Match($bn, 'sdom\d+')
            if (-not ($mm.Success -and $validOggBase.Contains($mm.Value))) { return $true }
        }
    }
    return $false
}

# ---- load the used-set ----
# NB: variable name must not case-collide with the -Used param (PowerShell vars are case-insensitive).
$usedSet = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($line in [System.IO.File]::ReadLines($Used)) {
    $p = $line.Trim()
    if ($p.Length -eq 0) { continue }
    [void]$usedSet.Add((Rel $p $UsedRoot))
}
Write-Host "[prune] used-set   : $($usedSet.Count) distinct file(s) from $Used"
Write-Host "[prune] data root  : $Data"
Write-Host "[prune] quarantine : $Quarantine"
Write-Host "[prune] mode       : $(if ($Execute) {'EXECUTE (move to quarantine)'} else {'DRY-RUN (report only)'})"
Write-Host ''

# ---- classify every file under DATA ----
$byFolder = @{}   # top-folder -> [pscustomobject]{TotMB TotN DeadMB DeadN}
$candidates = New-Object System.Collections.Generic.List[object]
Get-ChildItem -LiteralPath $Data -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
    $rel = Rel $_.FullName $Data
    $top = ($rel -split '\\', 2)[0]
    if (-not $byFolder.ContainsKey($top)) { $byFolder[$top] = [pscustomobject]@{ Folder=$top; TotMB=[double]0; TotN=0; DeadMB=[double]0; DeadN=0 } }
    $mb = $_.Length / 1MB
    $byFolder[$top].TotMB += $mb; $byFolder[$top].TotN++
    if (-not (IsForceDead $rel) -and ($usedSet.Contains($rel) -or (IsKept $rel))) { return }
    $byFolder[$top].DeadMB += $mb; $byFolder[$top].DeadN++
    $candidates.Add([pscustomobject]@{ Rel=$rel; Full=$_.FullName; MB=$mb })
}

$byFolder.Values | Sort-Object DeadMB -Descending |
    Select-Object Folder,
        @{n='Total_MB';e={[math]::Round($_.TotMB,1)}}, @{n='Total_N';e={$_.TotN}},
        @{n='Dead_MB'; e={[math]::Round($_.DeadMB,1)}}, @{n='Dead_N'; e={$_.DeadN}},
        @{n='Dead_%';  e={ if($_.TotN){[math]::Round(100.0*$_.DeadN/$_.TotN,1)}else{0} }} |
    Format-Table -AutoSize

$deadMB = ($candidates | Measure-Object MB -Sum).Sum
Write-Host ("[prune] DEAD candidates: {0:N0} file(s), {1:N1} MB ({2:N2} GB)" -f $candidates.Count, $deadMB, ($deadMB/1024))

# always write the candidate manifest beside the used-set (so the list is reviewable before -Execute)
$manifest = [System.IO.Path]::ChangeExtension($Used, $null).TrimEnd('.') + '.dead.txt'
[System.IO.File]::WriteAllLines($manifest, [string[]]@($candidates | ForEach-Object { $_.Rel }))
Write-Host "[prune] candidate list -> $manifest"

if ($candidates.Count -eq 0) { Write-Host "[prune] nothing to prune (0 candidates)."; return }

if (-not $Execute) {
    Write-Host ''
    Write-Host "[prune] dry-run only. Review the list above, then re-run with -Execute to MOVE them to quarantine."
    Write-Host "[prune] undo any time with:  -Restore -Data '$Data'"
    return
}

# ---- execute: MOVE (not delete) into quarantine, preserving layout ----
New-Item -ItemType Directory -Force -Path $Quarantine | Out-Null
$moved = 0
foreach ($c in $candidates) {
    $dst = Join-Path $Quarantine $c.Rel
    New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
    Move-Item -LiteralPath $c.Full -Destination $dst -Force
    $moved++
    if ($moved % 2000 -eq 0) { Write-Host "[prune]   moved $moved / $($candidates.Count) ..." }
}
Write-Host ''
Write-Host ("[prune] MOVED {0:N0} file(s) ({1:N1} MB) -> {2}" -f $moved, $deadMB, $Quarantine)
Write-Host "[prune] now repackage + smoke-test. If anything broke:  -Restore -Data '$Data'"
Write-Host "[prune] once verified good, delete the quarantine folder to reclaim the space."
exit 0
