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

  DATA (the runtime asset pack) is NO LONGER assembled by Unity's built-in PackageData (that runs
  package_build.ps1 off the RAW assets, which only exist in the git MAIN worktree). Instead, after a
  successful build this script copies DATA beside the exe from -Source (an already-assembled flat DATA
  tree; default the clean pack H:\65_remake_clean\DATA) — so you can produce "exe + full DATA" from ANY
  worktree. Unity is launched with SDO_SKIP_PACKAGE=1 to skip its internal packer.

.PARAMETER Unity
  Path to Unity.exe. Default: newest editor found under the Unity Hub install dir.

.PARAMETER ProjectPath
  Unity project to build (= which worktree's project). Default: <repo>\65\My project.

.PARAMETER LogFile
  Build log path. Default: <repo>\build.log (truncated at start of each run).

.PARAMETER Source
  Already-assembled flat DATA tree, copied beside the exe (PROFILE seeded, not overwritten; *.bak* skipped).
  Default: H:\65_remake_clean\DATA.

.PARAMETER BuildOut
  Output folder (exe + DATA). Passed to Unity as -buildOut; DATA is assembled under it.
  Default: <repo>\Build\Windows.

.PARAMETER SkipData
  Build the exe only; do not assemble DATA.

.EXAMPLE
  ./tools/build_windows.ps1
.EXAMPLE
  ./tools/build_windows.ps1 -Unity "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe"
#>
[CmdletBinding()]
param(
    [string]$Unity,
    [string]$ProjectPath,
    [string]$LogFile,
    [string]$Source = 'H:\65_remake_clean\DATA',   # DATA 資產來源(clean 包 或 含 assets\ 的 raw repo);放到 exe 旁
    [string]$BuildOut,                             # 輸出資料夾(exe + DATA);預設 <repo>\Build\Windows
    [switch]$SkipData                             # 只出 exe,不組 DATA
)

$ErrorActionPreference = 'Stop'

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $ProjectPath) { $ProjectPath = Join-Path $Repo '65\My project' }
if (-not $LogFile)     { $LogFile     = Join-Path $Repo 'build.log' }
if (-not $BuildOut)    { $BuildOut    = Join-Path $Repo 'Build\Windows' }

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
Write-Host "[build] out     = $BuildOut"
Write-Host "[build] data    = $(if ($SkipData) {'(skip)'} else {$Source})"
Write-Host "[build] log     = $LogFile"
Write-Host ""

if (Test-Path $LogFile) { Remove-Item $LogFile -Force }

# 讓 Unity 內建的 PackageData 跳過 —— 它會呼叫 package_build.ps1 從原始 assets 組 DATA,
# 而原始 assets 只有主 worktree 有。DATA 改由本腳本 build 成功後,從 -Source(預設 clean 包)組到 exe 旁。
$env:SDO_SKIP_PACKAGE = '1'

# NOTE: Start-Process -ArgumentList (PS 5.1) does NOT auto-quote array elements that contain
# spaces — "H:\65_remake\65\My project" would get split into "...\My" + "project". Embed the
# quotes ourselves around any path argument.
$p = Start-Process -FilePath $Unity -PassThru -NoNewWindow -ArgumentList @(
    '-batchmode', '-nographics', '-quit',
    '-projectPath', "`"$ProjectPath`"",
    '-executeMethod', 'BuildScript.BuildWindows',
    '-buildOut', "`"$BuildOut`"",
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
Remove-Item Env:\SDO_SKIP_PACKAGE -ErrorAction SilentlyContinue

# ---- copy DATA beside the exe from -Source (default clean pack) — self-contained, no other script needed ----
# -Source 是已整理好的攤平 DATA 樹(clean 包)。整棵複製到 <BuildOut>\DATA:排除 *.bak*;PROFILE 另 seed(不覆蓋既有存檔)。
if ($code -eq 0 -and -not $SkipData) {
    if (-not (Test-Path $Source)) {
        Write-Host "[build] WARNING: -Source not found; DATA not assembled: $Source" -ForegroundColor Yellow
    } else {
        $dataOut   = Join-Path $BuildOut 'DATA'
        $profileXd = Join-Path $Source 'PROFILE'
        Write-Host ""
        Write-Host "[build] packaging DATA: $Source -> $dataOut"
        New-Item -ItemType Directory -Force -Path $dataOut | Out-Null
        & robocopy $Source $dataOut /E /XF *.bak* /XD $profileXd /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
        if ($LASTEXITCODE -ge 8) { Write-Host "[build] WARNING: DATA robocopy exit=$LASTEXITCODE" -ForegroundColor Yellow }
        if (Test-Path $profileXd) {
            & robocopy $profileXd (Join-Path $dataOut 'PROFILE') /E /XC /XN /XO /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
        }
        Write-Host "[build] DATA ready: $dataOut"
    }
}

$color = if ($code -eq 0) { 'Green' } else { 'Red' }
Write-Host ""
Write-Host "=== Unity exit code: $code ===" -ForegroundColor $color
exit $code
