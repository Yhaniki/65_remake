<#
.SYNOPSIS
  Copy ONLY the assets the remake actually uses into a clean folder, preserving the assets/-relative
  layout so the result is a drop-in replacement for assets/ (editor + build resolve unchanged).

.DESCRIPTION
  Used-set derived by static loader tracing — see docs/USED_ASSETS.md. Leaves the originals untouched
  (pure copy). Default is a DRY-RUN report (sizes only); pass -Execute to actually copy.

  Copies:
    sdox_offline/Extracted            (Root, whole tree; UI/BGM -> DATA/BGM)   ~1.6 GB
    sdox_offline/SE                   (-> DATA/SE)                               ~52 MB    [skip with -NoAudio]
    sdox_offline/music               (-> DATA/MUSIC)                            ~8.3 GB   [only with -IncludeMusic]
    Datas/AVATAR                      (full costume mesh+texture catalog)        ~4.0 GB
    <online>/DatasSDO/UI/{9 folders}  (line-up UI art overlay)                   ~385 MB
    <online>/DatasSDO/LOADING         (gameplay loading screens)                 ~12 MB
    <online>/iteminfo.dat, setinfo.dat (shop item data)

  <online> = the assets/ subdir that holds DatasSDO/UI/MUSIC/ICONS (the oddly-named 閉撰敃氪 folder);
  located by scan, not hardcoded. The clean copy keeps that same folder name so *Art.cs resolvers still match.

.PARAMETER Dest
  Target folder for the clean copy (required to -Execute). Needs ~5.5 GB free (or ~14 GB with -IncludeMusic).

.PARAMETER IncludeMusic   Include the 8.3 GB sdox_offline/music tree.
.PARAMETER NoAudio        Skip SE. (The lobby bgm ships from Extracted/UI/BGM, which is inside the Extracted tree;
                          the dead top-level sdox_offline/BGM set is not collected at all.)
.PARAMETER Execute        Actually copy. Without it, only prints the plan + sizes.
#>
[CmdletBinding()]
param(
    [string]$Dest,
    [switch]$IncludeMusic,
    [switch]$NoAudio,
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
$Repo   = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Assets = Join-Path $Repo 'assets'
if (-not (Test-Path $Assets)) { throw "assets/ not found at $Assets" }

# Locate the online client dir (holds DatasSDO) by content, not by its odd name.
$online = Get-ChildItem -LiteralPath $Assets -Directory -ErrorAction SilentlyContinue |
          Where-Object { Test-Path (Join-Path $_.FullName 'DatasSDO\UI\MUSIC\ICONS') } |
          Select-Object -First 1
if (-not $online) { throw "Could not find the online DatasSDO folder under $Assets" }
$onlineRel = $online.Name

# Build the used-set as (relative-to-assets path, kind). Order = the report order.
$items = New-Object System.Collections.Generic.List[object]
function Add-Item($rel, $kind) { $items.Add([pscustomobject]@{ Rel = $rel; Kind = $kind }) }

Add-Item 'sdox_offline\Extracted' 'dir'
if (-not $NoAudio) { Add-Item 'sdox_offline\SE' 'dir' }   # lobby bgm rides inside Extracted (UI/BGM); dead sdox_offline/BGM not collected
if ($IncludeMusic) { Add-Item 'sdox_offline\music' 'dir' }
Add-Item 'Datas\AVATAR' 'dir'
foreach ($u in 'UI\MUSIC\ICONS','UI\SHOP','UI\EXPRESSIONS','UI\BUBBLE2','UI\MYHOUSEDLG',
               'UI\STATIS\STATISTIC','UI\ROOMDLG','UI\OPTIONDLG','UI\LOBBYDLG\KEYS','LOADING') {
    Add-Item (Join-Path (Join-Path $onlineRel 'DatasSDO') $u) 'dir'
}
Add-Item (Join-Path $onlineRel 'iteminfo.dat') 'file'
Add-Item (Join-Path $onlineRel 'setinfo.dat')  'file'

# ---- report ----
Write-Host "[collect] repo   = $Repo"
Write-Host "[collect] online = $onlineRel"
Write-Host "[collect] dest   = $(if ($Dest) { $Dest } else { '(dry-run, none)' })"
Write-Host ("[collect] mode   = {0}" -f ($(if ($Execute) { 'EXECUTE (copy)' } else { 'DRY-RUN (report only)' })))
Write-Host ''

$total = [long]0; $totalFiles = [long]0
$plan = foreach ($it in $items) {
    $src = Join-Path $Assets $it.Rel
    $exists = Test-Path -LiteralPath $src
    $mb = 0.0; $n = 0
    if ($exists) {
        if ($it.Kind -eq 'dir') {
            $s = Get-ChildItem -LiteralPath $src -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum
            $mb = [math]::Round($s.Sum / 1MB, 1); $n = $s.Count
        } else {
            $mb = [math]::Round((Get-Item -LiteralPath $src).Length / 1MB, 2); $n = 1
        }
        $total += [long]($mb * 1MB); $totalFiles += $n
    }
    [pscustomobject]@{ Item = $it.Rel; MB = $mb; Files = $n; Present = $exists }
}
$plan | Format-Table -AutoSize
Write-Host ("[collect] TOTAL {0:N1} MB ({1:N2} GB) across {2:N0} files" -f ($total/1MB), ($total/1GB), $totalFiles)

if (-not $Execute) {
    Write-Host ''
    Write-Host "[collect] dry-run only. Re-run with:  -Execute -Dest '<target>'  (add -IncludeMusic for the 8.3 GB songs)."
    return
}

# ---- execute ----
if (-not $Dest) { throw "-Execute requires -Dest <target folder>" }
New-Item -ItemType Directory -Force -Path $Dest | Out-Null

function Copy-Tree($src, $dst) {
    & robocopy $src $dst /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $src (exit $LASTEXITCODE)" }
}

foreach ($it in $items) {
    $src = Join-Path $Assets $it.Rel
    if (-not (Test-Path -LiteralPath $src)) { Write-Warning "[collect] skip missing: $($it.Rel)"; continue }
    $dst = Join-Path $Dest $it.Rel
    Write-Host "[collect] copy $($it.Rel)"
    if ($it.Kind -eq 'dir') {
        Copy-Tree $src $dst
    } else {
        New-Item -ItemType Directory -Force -Path (Split-Path $dst -Parent) | Out-Null
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }
}
Write-Host ''
Write-Host "[collect] done -> $Dest"
Write-Host "[collect] this folder mirrors assets/; it can replace assets/ (editor + build resolve unchanged)."
# robocopy sets $LASTEXITCODE to 1 ("files copied") on success; force a clean 0 so callers don't see a false failure.
exit 0
