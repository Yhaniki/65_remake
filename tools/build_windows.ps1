<#
.SYNOPSIS
  Run the Unity Windows player build (BuildScript.BuildWindows) and stream its log live,
  then report the exit code. Solves "Unity 直接跳出、看不到進度" — the batchmode build only
  writes to -logFile, and Unity.exe (a GUI-subsystem exe) does not block the PowerShell prompt.

.DESCRIPTION
  Launches Unity in -batchmode -nographics -quit, tails build.log in real time using a
  shared (FileShare.ReadWrite) reader — Unity keeps an exclusive-ish lock that breaks the
  plain `Get-Content -Wait`, so we open the file the .NET way — and waits for the process
  to exit. Prints the exit code at the end (0 = success, non-zero = failure; look at the log
  tail for the error).

  All paths derive from the repo root ($PSScriptRoot\..) — no hardcoded drive letters.

.PARAMETER Unity
  Path to Unity.exe. Default: newest editor found under the Unity Hub install dir.

.PARAMETER ProjectPath
  Unity project folder. Default: <repo>\65\My project.

.PARAMETER LogFile
  Build log path. Default: <repo>\build.log (truncated at start of each run).

.EXAMPLE
  ./tools/build_windows.ps1
.EXAMPLE
  ./tools/build_windows.ps1 -Unity "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe"
#>
[CmdletBinding()]
param(
    [string]$Unity,
    [string]$ProjectPath,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $ProjectPath) { $ProjectPath = Join-Path $Repo '65\My project' }
if (-not $LogFile)     { $LogFile     = Join-Path $Repo 'build.log' }

# Locate Unity.exe: use -Unity if given, else pick the newest editor under the Hub.
if (-not $Unity) {
    $hub = 'C:\Program Files\Unity\Hub\Editor'
    if (Test-Path $hub) {
        $Unity = Get-ChildItem -Path $hub -Filter Unity.exe -Recurse -ErrorAction SilentlyContinue |
                 Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $Unity -or -not (Test-Path $Unity)) {
    throw "Unity.exe not found. Pass -Unity 'C:\path\to\Unity.exe'."
}
if (-not (Test-Path $ProjectPath)) { throw "ProjectPath not found: $ProjectPath" }

Write-Host "[build] unity   = $Unity"
Write-Host "[build] project = $ProjectPath"
Write-Host "[build] log     = $LogFile"
Write-Host ""

if (Test-Path $LogFile) { Remove-Item $LogFile -Force }

# NOTE: Start-Process -ArgumentList (PS 5.1) does NOT auto-quote array elements that contain
# spaces — "H:\65_remake\65\My project" would get split into "...\My" + "project". Embed the
# quotes ourselves around any path argument.
$p = Start-Process -FilePath $Unity -PassThru -NoNewWindow -ArgumentList @(
    '-batchmode', '-nographics', '-quit',
    '-projectPath', "`"$ProjectPath`"",
    '-executeMethod', 'BuildScript.BuildWindows',
    '-logFile', "`"$LogFile`""
)
# Cache the handle NOW, or $p.ExitCode comes back null after the process exits (PS 5.1 quirk).
$null = $p.Handle

# Wait for Unity to create the log, then follow it with a shared reader while the process runs.
while (-not (Test-Path $LogFile) -and -not $p.HasExited) { Start-Sleep -Milliseconds 200 }

if (Test-Path $LogFile) {
    $fs = [System.IO.File]::Open($LogFile, 'Open', 'Read', 'ReadWrite')
    $sr = New-Object System.IO.StreamReader($fs)
    try {
        while (-not $p.HasExited) {
            $line = $sr.ReadLine()
            if ($null -ne $line) { Write-Host $line } else { Start-Sleep -Milliseconds 200 }
        }
        while ($null -ne ($line = $sr.ReadLine())) { Write-Host $line }   # drain remaining lines
    } finally {
        $sr.Dispose(); $fs.Dispose()
    }
}

$p.WaitForExit()
$code = $p.ExitCode
$color = if ($code -eq 0) { 'Green' } else { 'Red' }
Write-Host ""
Write-Host "=== Unity exit code: $code ===" -ForegroundColor $color
exit $code
