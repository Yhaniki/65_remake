<#
.SYNOPSIS
    比對新舊兩個 DATA 包，找出「新增」與「有修改」的檔案，
    並依原始資料夾結構複製到一個獨立的更新包資料夾。

.DESCRIPTION
    以「新包」為基準，逐檔和「舊包」對應路徑比對：
      - 舊包沒有這個檔案            -> Added    (新增)
      - 兩邊都有但內容不同          -> Modified (修改)
      - 兩邊都有且內容相同          -> Same     (略過)
    另外會列出「舊包有、新包沒有」的 Removed(刪除)，僅記錄不複製。

    比對方式(預設完整)：先比檔案大小，大小不同直接判定為 Modified(免算 hash)；
    大小相同才計算 SHA256 內容雜湊做確認。可用 -Threads 開多執行緒平行處理
    (NVMe SSD + 多核時效果最好；機械硬碟建議 -Threads 1)。
    若只想靠「大小+修改時間」更快速比(不算 hash)，加 -Fast。

.PARAMETER OldRoot
    舊 DATA 包的根目錄。

.PARAMETER NewRoot
    新 DATA 包的根目錄。

.PARAMETER OutRoot
    更新包輸出目錄(會保留相對路徑結構)。預設 .\DATA_update

.PARAMETER Threads
    平行執行緒數。預設為 min(邏輯核心數, 8)。設 1 = 單執行緒。

.PARAMETER Fast
    以「大小 + 最後修改時間」比對，不計算 hash(較快、較不嚴謹)。

.PARAMETER ReportOnly
    只產生報表、不實際複製檔案(先看清單再決定)。

.EXAMPLE
    .\Compare-DataPacks.ps1 -OldRoot "D:\old\DATA" -NewRoot "D:\new\DATA"

.EXAMPLE
    .\Compare-DataPacks.ps1 -OldRoot D:\old\DATA -NewRoot D:\new\DATA -Threads 10
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $OldRoot,
    [Parameter(Mandatory = $true)] [string] $NewRoot,
    [string] $OutRoot = ".\DATA_update",
    [int]    $Threads = [Math]::Min([Environment]::ProcessorCount, 8),
    [switch] $Fast,
    [switch] $ReportOnly
)

$ErrorActionPreference = 'Stop'
if ($Threads -lt 1) { $Threads = 1 }

function Resolve-RootPath([string]$p, [string]$label) {
    if (-not (Test-Path -LiteralPath $p)) { throw "$label 路徑不存在: $p" }
    (Resolve-Path -LiteralPath $p).Path.TrimEnd('\')
}

$OldRoot = Resolve-RootPath $OldRoot 'OldRoot(舊包)'
$NewRoot = Resolve-RootPath $NewRoot 'NewRoot(新包)'
# 一律建立輸出目錄(至少要放 update_manifest.csv)；ReportOnly 時裡面就只有報表
if (-not (Test-Path -LiteralPath $OutRoot)) {
    New-Item -ItemType Directory -Path $OutRoot -Force | Out-Null
}

Write-Host "舊包   : $OldRoot"
Write-Host "新包   : $NewRoot"
Write-Host "輸出   : $OutRoot"
Write-Host ("比對   : {0}" -f $(if ($Fast) { '大小+修改時間 (Fast)' } else { '大小 + SHA256 內容雜湊' }))
Write-Host ("執行緒 : {0}" -f $Threads)
Write-Host ("模式   : {0}" -f $(if ($ReportOnly) { '只出報表，不複製' } else { '複製新增/修改檔案' }))
Write-Host ("-" * 60)

# 每個執行緒處理一批新檔的工作邏輯 -------------------------------------------
$chunkScript = {
    param($files, $OldRoot, $NewRoot, $OutRoot, $Fast, $ReportOnly, $sync, $wi)

    $added = 0; $modified = 0; $same = 0; $copied = 0; $n = 0
    $rows = New-Object System.Collections.Generic.List[object]

    foreach ($nf in $files) {
        $n++
        if (($n -band 63) -eq 0) { $sync.Prog[$wi] = $n }

        $rel     = $nf.FullName.Substring($NewRoot.Length).TrimStart('\')
        $oldPath = Join-Path $OldRoot $rel
        $status  = $null

        if (-not (Test-Path -LiteralPath $oldPath)) {
            $status = 'Added'; $added++
        }
        else {
            $oldItem  = Get-Item -LiteralPath $oldPath -Force
            $sameFile = $false
            if ($oldItem.Length -eq $nf.Length) {
                if ($Fast) {
                    $sameFile = ($oldItem.LastWriteTimeUtc -eq $nf.LastWriteTimeUtc)
                }
                else {
                    $h1 = (Get-FileHash -LiteralPath $oldPath -Algorithm SHA256).Hash
                    $h2 = (Get-FileHash -LiteralPath $nf.FullName -Algorithm SHA256).Hash
                    $sameFile = ($h1 -eq $h2)
                }
            }
            if ($sameFile) { $same++; continue }
            $status = 'Modified'; $modified++
        }

        $rows.Add([pscustomobject]@{ Status = $status; RelativePath = $rel; Bytes = $nf.Length })

        if (-not $ReportOnly) {
            $dest    = Join-Path $OutRoot $rel
            $destDir = Split-Path $dest -Parent
            if (-not (Test-Path -LiteralPath $destDir)) {
                try { New-Item -ItemType Directory -Path $destDir -Force | Out-Null } catch {}
            }
            Copy-Item -LiteralPath $nf.FullName -Destination $dest -Force
            $copied++
        }
    }
    $sync.Prog[$wi] = $n
    [pscustomobject]@{ Added = $added; Modified = $modified; Same = $same; Copied = $copied; Rows = $rows }
}

# 掃描新包 + 分批 -----------------------------------------------------------
Write-Host "掃描新包..."
$newFiles = Get-ChildItem -LiteralPath $NewRoot -Recurse -File -Force
$total = $newFiles.Count
Write-Host ("  新包檔案數: {0}" -f $total)

$chunks = @{}
for ($t = 0; $t -lt $Threads; $t++) { $chunks[$t] = New-Object System.Collections.Generic.List[object] }
for ($idx = 0; $idx -lt $total; $idx++) { $chunks[$idx % $Threads].Add($newFiles[$idx]) }

$sync = [hashtable]::Synchronized(@{})
$sync.Prog = New-Object 'int[]' $Threads

# 開執行緒池平行比對 --------------------------------------------------------
$iss  = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
$pool = [runspacefactory]::CreateRunspacePool(1, $Threads, $iss, $Host)
$pool.Open()

$jobs = @()
for ($t = 0; $t -lt $Threads; $t++) {
    $ps = [powershell]::Create()
    $ps.RunspacePool = $pool
    [void]$ps.AddScript($chunkScript.ToString()).
        AddArgument($chunks[$t].ToArray()).
        AddArgument($OldRoot).AddArgument($NewRoot).AddArgument($OutRoot).
        AddArgument([bool]$Fast).AddArgument([bool]$ReportOnly).
        AddArgument($sync).AddArgument($t)
    $jobs += [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() }
}

while ($jobs.Handle.IsCompleted -contains $false) {
    $done = ($sync.Prog | Measure-Object -Sum).Sum
    $pct  = if ($total -gt 0) { [int](($done / $total) * 100) } else { 100 }
    Write-Progress -Activity "平行比對中 ($Threads 執行緒)" -Status "$done / $total" -PercentComplete $pct
    Start-Sleep -Milliseconds 200
}
Write-Progress -Activity "平行比對中" -Completed

$added = 0; $modified = 0; $same = 0; $copied = 0
$manifest = New-Object System.Collections.Generic.List[object]
foreach ($j in $jobs) {
    $res = $j.PS.EndInvoke($j.Handle)
    $o = $res[0]
    $added += $o.Added; $modified += $o.Modified; $same += $o.Same; $copied += $o.Copied
    if ($o.Rows) { $manifest.AddRange([object[]]$o.Rows) }
    $j.PS.Dispose()
}
$pool.Close(); $pool.Dispose()

# 找刪除項(舊有、新無) ------------------------------------------------------
Write-Host "掃描舊包(找刪除項)..."
$removed = 0
$oldFiles = Get-ChildItem -LiteralPath $OldRoot -Recurse -File -Force
foreach ($of in $oldFiles) {
    $rel = $of.FullName.Substring($OldRoot.Length).TrimStart('\')
    if (-not (Test-Path -LiteralPath (Join-Path $NewRoot $rel))) {
        $manifest.Add([pscustomobject]@{ Status = 'Removed'; RelativePath = $rel; Bytes = $of.Length })
        $removed++
    }
}

# 報表 ----------------------------------------------------------------------
$reportDir = (Resolve-Path -LiteralPath $OutRoot).Path
$reportCsv = Join-Path $reportDir 'update_manifest.csv'
$manifest | Export-Csv -LiteralPath $reportCsv -NoTypeInformation -Encoding UTF8

Write-Host ("=" * 60)
Write-Host ("新增 Added    : {0}" -f $added)
Write-Host ("修改 Modified : {0}" -f $modified)
Write-Host ("相同 Same     : {0}" -f $same)
Write-Host ("刪除 Removed  : {0} (僅記錄，未處理)" -f $removed)
if (-not $ReportOnly) { Write-Host ("已複製到更新包 : {0} 個檔案 -> {1}" -f $copied, $reportDir) }
Write-Host ("報表 : {0}" -f $reportCsv)
