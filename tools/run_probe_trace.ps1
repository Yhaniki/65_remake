<#
.SYNOPSIS
  Run the UsedAssetsProbe against a DATA tree and produce its USED-SET (the files any code path opens), for
  tools/prune_dead_data.ps1. Two capture channels:
    1) SELF-LOG  — the probe writes every file it opens (env SDO_PROBE_LOG). Always works, no elevation. Ground truth.
    2) PROCMON   — an independent OS-level trace of dance.exe's reads under DATA (-UseProcmon). Cross-check. Needs admin.
  The final used_files.txt is the UNION of whatever channels ran; if both ran, discrepancies are reported.

.DESCRIPTION
  The probe overrides SdoExtracted.Root to -Data (via SDO_PROBE=<path>), so the used-set maps 1:1 onto the tree
  you are about to prune — no 13 GB copy / junction needed. The built player supplies its own StreamingAssets
  (song_table.csv), so it enumerates the full song list regardless of which DATA is pointed at.

.PARAMETER Exe        Path to the built dance.exe (the probe player). Required.
.PARAMETER Data       DATA tree to probe (== the tree being pruned).         Default H:\65_remake_clean\DATA
.PARAMETER Out        Final used-set file.                    Default <Data>\..\used_files.txt
.PARAMETER WorkDir    Scratch for self-log / pml / csv.       Default <Data>\..\_probe_work
.PARAMETER UseProcmon Also run a Process Monitor trace as an independent cross-check (needs admin).
.PARAMETER Procmon    procmon.exe path (auto-resolved if omitted).
.PARAMETER TimeoutSec Max seconds to wait for the probe to finish.           Default 3600
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [string]$Data = 'H:\65_remake_clean\DATA',
    [string]$Out,
    [string]$WorkDir,
    [switch]$UseProcmon,
    [string]$Procmon,
    [int]$TimeoutSec = 3600
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe))  { throw "dance.exe not found: $Exe" }
if (-not (Test-Path -LiteralPath $Data)) { throw "DATA not found: $Data" }
$Exe  = (Resolve-Path -LiteralPath $Exe).Path
$Data = (Resolve-Path -LiteralPath $Data).Path.TrimEnd('\')
if (-not $Out)     { $Out     = Join-Path (Split-Path $Data -Parent) 'used_files.txt' }
if (-not $WorkDir) { $WorkDir = Join-Path (Split-Path $Data -Parent) '_probe_work' }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$selfLog = Join-Path $WorkDir 'probe_selflog.txt'
$pml     = Join-Path $WorkDir 'probe.pml'
$csv     = Join-Path $WorkDir 'probe.csv'
$pmUsed  = Join-Path $WorkDir 'procmon_used.txt'

function Rel($full) {
    if ($full.ToLowerInvariant().StartsWith(($Data.ToLowerInvariant() + '\'))) { $full = $full.Substring($Data.Length + 1) }
    return $full.TrimStart('\').ToLowerInvariant() -replace '/', '\'
}
function Resolve-Procmon {
    if ($Procmon -and (Test-Path -LiteralPath $Procmon)) { return (Resolve-Path $Procmon).Path }
    $c = Get-Command 'procmon.exe' -ErrorAction SilentlyContinue; if ($c) { return $c.Source }
    foreach ($p in @("$env:LOCALAPPDATA\Microsoft\WinGet\Links\procmon.exe",
                     "$env:LOCALAPPDATA\Microsoft\WinGet\Links\Procmon64.exe")) {
        if (Test-Path -LiteralPath $p) { return $p }
    }
    return $null
}

Write-Host "[trace] exe   = $Exe"
Write-Host "[trace] data  = $Data"
Write-Host "[trace] work  = $WorkDir"
Write-Host "[trace] out   = $Out"
Write-Host ''

# ---- optional: start Procmon capture (best-effort; self-log is the fallback) ----
$pm = $null
if ($UseProcmon) {
    $pm = Resolve-Procmon
    if (-not $pm) { Write-Warning "[trace] procmon not found — continuing with self-log only." }
    else {
        Remove-Item -LiteralPath $pml -ErrorAction SilentlyContinue
        Write-Host "[trace] starting procmon capture -> $pml"
        try {
            Start-Process -FilePath $pm -ArgumentList '/AcceptEula','/Quiet','/Minimized','/BackingFile',"`"$pml`"" | Out-Null
            Start-Sleep -Seconds 4    # let the driver attach + begin capturing
        } catch { Write-Warning "[trace] procmon start failed ($($_.Exception.Message)) — self-log only."; $pm = $null }
    }
}

# ---- run the probe player ----
Remove-Item -LiteralPath $selfLog -ErrorAction SilentlyContinue
$env:SDO_PROBE     = $Data       # probe overrides SdoExtracted.Root to this tree
$env:SDO_PROBE_LOG = $selfLog    # probe self-records every opened file here
$runLog = Join-Path $WorkDir 'probe_run.log'
Write-Host "[trace] launching probe (headless): SDO_PROBE=$Data"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
# standalone player runs the game loop + coroutines under -batchmode; the probe makes no graphics objects so -nographics is safe.
$proc = Start-Process -FilePath $Exe -ArgumentList '-batchmode','-nographics','-logfile',"`"$runLog`"" -PassThru
if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    Write-Warning "[trace] probe did not exit within $TimeoutSec s — killing."
    try { $proc.Kill() } catch {}
}
$sw.Stop()
Remove-Item Env:\SDO_PROBE, Env:\SDO_PROBE_LOG -ErrorAction SilentlyContinue
Write-Host ("[trace] probe finished in {0:N0}s (exit {1})" -f $sw.Elapsed.TotalSeconds, $proc.ExitCode)
Start-Sleep -Seconds 2

# ---- stop + export Procmon, filter to dance.exe reads under DATA ----
$pmSet = $null
if ($pm) {
    try {
        Write-Host "[trace] stopping procmon + exporting csv ..."
        Start-Process -FilePath $pm -ArgumentList '/Terminate' -Wait
        Start-Sleep -Seconds 2
        if (Test-Path -LiteralPath $pml) {
            Start-Process -FilePath $pm -ArgumentList '/OpenLog',"`"$pml`"",'/SaveAs',"`"$csv`"" -Wait
            $exeName = Split-Path $Exe -Leaf
            $pmSet = New-Object 'System.Collections.Generic.HashSet[string]'
            Import-Csv -LiteralPath $csv | Where-Object {
                $_.'Process Name' -eq $exeName -and $_.Result -eq 'SUCCESS' -and
                ($_.Operation -eq 'CreateFile' -or $_.Operation -eq 'ReadFile') -and
                $_.Path -and $_.Path.ToLowerInvariant().StartsWith(($Data.ToLowerInvariant() + '\'))
            } | ForEach-Object { [void]$pmSet.Add((Rel $_.Path)) }
            [System.IO.File]::WriteAllLines($pmUsed, $pmSet)
            Write-Host "[trace] procmon used-set: $($pmSet.Count) files -> $pmUsed"
        } else { Write-Warning "[trace] no pml produced (elevation?) — self-log only." }
    } catch { Write-Warning "[trace] procmon export failed: $($_.Exception.Message) — self-log only." }
}

# ---- load self-log ----
$selfSet = New-Object 'System.Collections.Generic.HashSet[string]'
if (Test-Path -LiteralPath $selfLog) {
    foreach ($l in [System.IO.File]::ReadLines($selfLog)) { $t = $l.Trim(); if ($t) { [void]$selfSet.Add((Rel $t)) } }
    Write-Host "[trace] self-log used-set: $($selfSet.Count) files"
} else { Write-Warning "[trace] no self-log produced — did the probe run? (check log.txt beside the exe)" }

# ---- union + reconcile ----
$union = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($x in $selfSet) { [void]$union.Add($x) }
if ($pmSet) { foreach ($x in $pmSet) { [void]$union.Add($x) } }
if ($union.Count -eq 0) { throw "[trace] used-set is EMPTY — aborting so prune can't nuke everything." }

if ($pmSet -and $selfSet.Count -gt 0) {
    $onlyPm   = @($pmSet   | Where-Object { -not $selfSet.Contains($_) })
    $onlySelf = @($selfSet | Where-Object { -not $pmSet.Contains($_)   })
    Write-Host ("[trace] reconcile: procmon-only={0}  selflog-only={1}  (both channels agree on {2})" -f $onlyPm.Count, $onlySelf.Count, ($union.Count - $onlyPm.Count - $onlySelf.Count))
    if ($onlyPm.Count)   { [System.IO.File]::WriteAllLines((Join-Path $WorkDir 'only_procmon.txt'), $onlyPm) }
    if ($onlySelf.Count) { [System.IO.File]::WriteAllLines((Join-Path $WorkDir 'only_selflog.txt'), $onlySelf) }
}

[System.IO.File]::WriteAllLines($Out, $union)
Write-Host ''
Write-Host ("[trace] DONE. used-set = {0} files -> {1}" -f $union.Count, $Out)
Write-Host "[trace] next:  tools\prune_dead_data.ps1 -Data '$Data' -Used '$Out'    (dry-run report; add -Execute to quarantine)"
exit 0
