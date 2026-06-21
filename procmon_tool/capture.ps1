<#
  ProcMon capture+analyze for SDO sound investigation.
  Captures ONLY the game process's file access, then reports which .wav/.ogg
  files were opened (and whether a specific file, e.g. SE_0031.wav, was used).

  REQUIRES an ELEVATED (Administrator) PowerShell — ProcMon loads a kernel driver.

  Examples:
    .\capture.ps1                       # game already running; capture until you press Enter
    .\capture.ps1 -Launch               # also launch the game first
    .\capture.ps1 -Highlight SE_0030.wav   # check a different file
    .\capture.ps1 -Proc other.exe       # capture a different process
#>
param(
  [string]$Proc      = "sdo_stand_alone.exe",
  [string]$Highlight = "SE_0031.wav",
  [switch]$Launch,
  [string]$GameExe   = "h:\65_remake\assets\sdox_offline\sdo_stand_alone.exe"
)
$ErrorActionPreference = "Stop"
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$filter = Join-Path $root "pm_filter.pmc"
$runs   = Join-Path $root "runs"
New-Item -ItemType Directory -Force -Path $runs | Out-Null
$pml    = Join-Path $runs ("cap_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".pml")

# --- admin check (ProcMon needs the driver) ---
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
  Write-Host "[!] Not elevated. Right-click PowerShell -> Run as administrator, then re-run." -ForegroundColor Red
  return
}

# --- locate Procmon.exe (winget install location) ---
$pm = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "Procmon.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
if (-not $pm) {
  Write-Host "[!] Procmon.exe not found. Install:  winget install Microsoft.Sysinternals.ProcessMonitor" -ForegroundColor Red
  return
}

# --- ensure the capture filter exists (only-this-process + drop the rest) ---
if (-not (Test-Path $filter)) {
  Write-Host "[*] Generating filter config..."
  python (Join-Path $root "make_filter.py") $filter $Proc
}

Write-Host "[*] ProcMon: $pm"
Write-Host "[*] Capturing ONLY '$Proc' -> $pml"
Start-Process -FilePath $pm -ArgumentList @("/AcceptEula","/Quiet","/Minimized","/LoadConfig",$filter,"/BackingFile",$pml)
Start-Sleep -Seconds 2

if ($Launch) {
  Write-Host "[*] Launching game..."
  Start-Process -FilePath $GameExe -WorkingDirectory (Split-Path -Parent $GameExe)
}

Write-Host ""
Write-Host ">>> Enter the scene you want to test FRESH (so the scene-load is captured)." -ForegroundColor Cyan
Write-Host ">>> Stay a few seconds, then press ENTER here to stop + analyze." -ForegroundColor Cyan
[void](Read-Host)

Write-Host "[*] Stopping capture..."
& $pm /Terminate | Out-Null
Start-Sleep -Seconds 2

Write-Host "[*] Analyzing (highlight: $Highlight)..."
Write-Host ("-" * 70)
python (Join-Path $root "pm_analyze.py") $pml $Highlight
Write-Host ("-" * 70)
Write-Host "[done] capture saved: $pml" -ForegroundColor Green
Write-Host "      re-analyze later:  python `"$($root)\pm_analyze.py`" `"$pml`" SomeFile.wav"
