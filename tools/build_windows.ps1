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

.PARAMETER Product
  Product word used in the final folder name. Default: Dance.

.PARAMETER Name
  Override the final folder name entirely (e.g. -Name 'Dance nightly'). Default: computed from git,
  same rules as the window title — on a tag "<Product> <tag>", after a tag "<Product> <tag>-dev-<hash5>".

.PARAMETER NoRename
  Leave the output in -BuildOut (<repo>\Build\Windows); do not rename to the versioned folder.

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
    [switch]$SkipData,                             # 只出 exe,不組 DATA
    [string]$Product = 'Dance',                    # 最終資料夾名的產品字,例:"Dance v1.7.0-dev-3c1e7"
    [string]$Name,                                 # 直接指定最終資料夾名(蓋掉 git 算出來的)
    [switch]$NoRename,                             # 不改名,結果留在 <repo>\Build\Windows

    # 組完 DATA 之後打包成 SDOPAK 分卷(toolsuild_pak.py)並刪掉散裝樹。
    # 預設關 —— 開發時散裝樹好查、改一個檔就生效。出貨才開。
    [switch]$Pack,
    # 打包時加密。⚠️ 混淆不是保護:金鑰必然在執行檔裡。見 docsrchitecture\data-packaging.md §5。
    [switch]$Encrypt
)

$ErrorActionPreference = 'Stop'

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $ProjectPath) { $ProjectPath = Join-Path $Repo '65\My project' }
# build log 也進 test-output\ —— 跟測試產物同一個地方，repo 根保持乾淨。
if (-not $LogFile) {
    $outDir = Join-Path $Repo 'test-output'
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    $LogFile = Join-Path $outDir 'build.log'
}
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

# 一行 git 輸出;git 不在、不是 repo、指令失敗都回 $null(不讓 build 因此爆掉)。
function Get-GitLine([string]$gitArgs) {
    try {
        $out = & git -C $Repo @($gitArgs -split ' +') 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $out) { return $null }
        return ([string]($out | Select-Object -First 1)).Trim()
    } catch { return $null }
}

# 最終資料夾名 —— 和視窗標題(BuildScript.ComputeWindowTitle / Sdo.Game.BuildTitle)同一套規則,
# 這樣「資料夾名」和「exe 視窗標題」永遠是同一串,拿到包就知道是哪個 commit。
function Get-BuildFolderName([string]$product) {
    $exact   = Get-GitLine 'describe --tags --exact-match HEAD'   # HEAD 正好在 tag 上才有值
    $nearest = Get-GitLine 'describe --tags --abbrev=0'           # 最近的祖先 tag
    $hash5   = Get-GitLine 'rev-parse --short=5 HEAD'
    if ($exact)               { return "$product $exact" }
    if ($nearest -and $hash5) { return "$product $nearest-dev-$hash5" }
    if ($hash5)               { return "$product dev-$hash5" }
    return $product
}

$FinalDir = $null
if (-not $NoRename) {
    $finalName = if ($Name) { $Name } else { Get-BuildFolderName $Product }
    $FinalDir  = Join-Path (Split-Path $BuildOut -Parent) $finalName
    if ($FinalDir -eq $BuildOut) { $FinalDir = $null }
}

Write-Host "[build] unity   = $Unity"
Write-Host "[build] project = $ProjectPath"
Write-Host "[build] out     = $BuildOut"
Write-Host "[build] final   = $(if ($FinalDir) { $FinalDir } else { '(no rename)' })"
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
        # MMD 模型:clean 包裡沒有(它是「官方資產整理過的那一份」),但打包版要能跑 MMD 顯示就一定要有
        # DATA\MODEL。package_build.ps1 有做這件事,可是這條路徑刻意跳過它(SDO_SKIP_PACKAGE=1),所以這裡自己補。
        # 原始 assets 只有主 worktree 有 —— 沒有就安靜跳過(其它 worktree 出的 exe 本來就沒模型可放)。
        $mmdSrc = Join-Path $Repo 'assets\MODEL'
        if (Test-Path $mmdSrc) {
            # ADDON/MODEL 才是正式位置（ADDON = 玩家自己丟東西的那棵樹）。順帶好處：ADDON 是 reserved 目錄、
            # 永遠不進 pak，所以模型放這裡自動就不會被打包。舊位置 <DATA>/MODEL 執行期仍然會掃（相容既有安裝）。
            $mmdOut = Join-Path $dataOut 'ADDON\MODEL'
            & robocopy $mmdSrc $mmdOut /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
            if ($LASTEXITCODE -ge 8) { Write-Host "[build] WARNING: MODEL robocopy exit=$LASTEXITCODE" -ForegroundColor Yellow }
            $n = (Get-ChildItem -LiteralPath $mmdOut -Directory -ErrorAction SilentlyContinue | Measure-Object).Count
            Write-Host "[build] MMD models: $mmdSrc -> $mmdOut ($n 個)"
        }
        Write-Host "[build] DATA ready: $dataOut"

        # ---- 打包成 SDOPAK 分卷 ----
        # 順序是刻意的:先把散裝 DATA 完整組好(clean 包 + MODEL 都放進去了),再一次打包 ——
        # 打包器看到的就是最終狀態,不必知道任何組裝規則。
        if ($Pack) {
            $pyPack = Join-Path $PSScriptRoot 'build_pak.py'
            if (-not (Test-Path $pyPack)) { throw "找不到 $pyPack" }

            # 🔴 manifest 要放在**出貨資料夾外面**。它是每一條路徑的明文(base_avatar 那份 5.4 MB)——
            #    跟著出貨等於把索引加密整個作廢,別人拿到就有完整檔案清單。
            #    放 $BuildOut 底下不夠遠:那個資料夾最後會被改名成 Build\Dance v<版本>\,
            #    正是要壓縮寄出去的東西。所以放到它的**上一層**(Build\pak_manifests\)。
            #    留著是因為下次產 patch 卷要拿它比對差異。
            $manifestDir = Join-Path (Split-Path -Parent $BuildOut) 'pak_manifests'
            Write-Host "[build] pack: $dataOut -> SDOPAK$(if ($Encrypt) { '(加密)' } else { '(明碼)' })"
            $packArgs = @($pyPack, '--source', $dataOut, '--out', $dataOut, '--manifest-dir', $manifestDir)
            if ($Encrypt) { $packArgs += '--encrypt' }
            $env:PYTHONIOENCODING = 'utf-8'
            & python @packArgs
            if ($LASTEXITCODE -ne 0) { throw "build_pak.py 失敗 exit=$LASTEXITCODE" }

            # 打包成功才刪散裝樹 —— 失敗時留著,至少 build 還是能跑的。
            # 🔴 **只刪 packed_dirs.json 說有打包的那些**。不能寫成「除了 PROFILE/ADDON/… 以外全刪」——
            #    BGM 刻意維持散裝(見 build_pak.py 的 VOLUMES),那種寫法會把它一起刪掉,
            #    症狀是「大廳完全沒有音樂而且不報錯」。清單由打包器產出,兩邊才不會漂移。
            $packedJson = Join-Path $manifestDir 'packed_dirs.json'
            if (-not (Test-Path $packedJson)) { throw "build_pak.py 沒有產生 packed_dirs.json —— 不敢刪散裝樹" }
            $packed = (Get-Content $packedJson -Raw -Encoding UTF8 | ConvertFrom-Json).packed

            Get-ChildItem -LiteralPath $dataOut -Directory | Where-Object { $packed -contains $_.Name } | ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
            }
            # 頂層零星檔(iteminfo.dat / shop_names.tsv…)已經進了 base_core.pak。出貨的 DATA 只留 *.pak。
            Get-ChildItem -LiteralPath $dataOut -File | Where-Object { $_.Extension -ne '.pak' } |
                ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

            $mb = ((Get-ChildItem -LiteralPath $dataOut -Filter *.pak | Measure-Object Length -Sum).Sum / 1MB)
            Write-Host ("[build] pack: {0} 卷,{1:N0} MB" -f (Get-ChildItem -LiteralPath $dataOut -Filter *.pak).Count, $mb)
        }
    }
}

# build 完清掉 Burst / IL2CPP 的 _DoNotShip 除錯資料夾(不發布;版號戳在名字上,每次 build 會累積一堆)
if ($code -eq 0) {
    Get-ChildItem -LiteralPath $BuildOut -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*_BurstDebugInformation_DoNotShip' -or $_.Name -like '*_BackUpThisFolder_*' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force; Write-Host "[build] removed $($_.Name)" }
}

# ---- 最後改名:<repo>\Build\Windows -> <repo>\Build\Dance v1.7.0-dev-<hash5> ----
# build 仍然出到固定的 Build\Windows(Unity 換 outputPath 會整包重打,固定路徑才吃得到增量),
# 全部組完才整個資料夾搬過去。同名的舊包直接換掉,但只在它確實長得像 build 輸出(有 dance.exe)時才動手。
if ($code -eq 0 -and $FinalDir -and (Test-Path $BuildOut)) {
    $moved = $true
    if (Test-Path $FinalDir) {
        if (Test-Path (Join-Path $FinalDir 'dance.exe')) {
            Write-Host "[build] replacing existing $FinalDir"
            Remove-Item -LiteralPath $FinalDir -Recurse -Force
        } else {
            Write-Host "[build] WARNING: $FinalDir exists and is not a build output; left result in $BuildOut" -ForegroundColor Yellow
            $moved = $false
        }
    }
    if ($moved) {
        Move-Item -LiteralPath $BuildOut -Destination $FinalDir
        Write-Host "[build] output -> $FinalDir"
    }
}

$color = if ($code -eq 0) { 'Green' } else { 'Red' }
Write-Host ""
Write-Host "=== Unity exit code: $code ===" -ForegroundColor $color
exit $code
