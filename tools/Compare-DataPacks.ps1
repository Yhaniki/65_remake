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

    比對方式(預設 Hash)：先比檔案大小，大小不同直接判定為 Modified(免算 hash)；
    大小相同才計算 SHA256 內容雜湊做確認 —— 這樣最準也不會慢在明顯不同的檔案上。
    若你確定只想靠「大小+修改時間」快速比(不算 hash)，加 -Fast。

.PARAMETER OldRoot
    舊 DATA 包的根目錄。

.PARAMETER NewRoot
    新 DATA 包的根目錄。

.PARAMETER OutRoot
    更新包輸出目錄(會保留相對路徑結構)。預設 .\DATA_update

.PARAMETER Fast
    以「大小 + 最後修改時間」比對，不計算 hash(較快、較不嚴謹)。

.PARAMETER ReportOnly
    只產生報表、不實際複製檔案(先看清單再決定)。

.EXAMPLE
    .\Compare-DataPacks.ps1 -OldRoot "D:\old\DATA" -NewRoot "D:\new\DATA"

.EXAMPLE
    .\Compare-DataPacks.ps1 -OldRoot D:\old\DATA -NewRoot D:\new\DATA -OutRoot D:\update -ReportOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $OldRoot,
    [Parameter(Mandatory = $true)] [string] $NewRoot,
    [string] $OutRoot = ".\DATA_update",
    [switch] $Fast,
    [switch] $ReportOnly
)

$ErrorActionPreference = 'Stop'

function Resolve-RootPath([string]$p, [string]$label) {
    if (-not (Test-Path -LiteralPath $p)) {
        throw "$label 路徑不存在: $p"
    }
    (Resolve-Path -LiteralPath $p).Path.TrimEnd('\')
}

$OldRoot = Resolve-RootPath $OldRoot 'OldRoot(舊包)'
$NewRoot = Resolve-RootPath $NewRoot 'NewRoot(新包)'

Write-Host "舊包 : $OldRoot"
Write-Host "新包 : $NewRoot"
Write-Host "輸出 : $OutRoot"
Write-Host ("比對 : {0}" -f $(if ($Fast) { '大小+修改時間 (Fast)' } else { '大小 + SHA256 內容雜湊' }))
Write-Host ("模式 : {0}" -f $(if ($ReportOnly) { '只出報表，不複製' } else { '複製新增/修改檔案' }))
Write-Host ("-" * 60)

# 建立新包所有檔案的相對路徑索引
Write-Host "掃描新包..."
$newFiles = Get-ChildItem -LiteralPath $NewRoot -Recurse -File -Force
Write-Host ("  新包檔案數: {0}" -f $newFiles.Count)

function Get-Sha256([string]$path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

# 內容/屬性比對：相同回 $true
function Test-SameFile($oldItem, $newItem) {
    if ($oldItem.Length -ne $newItem.Length) { return $false }
    if ($Fast) {
        return ($oldItem.LastWriteTimeUtc -eq $newItem.LastWriteTimeUtc)
    }
    return ((Get-Sha256 $oldItem.FullName) -eq (Get-Sha256 $newItem.FullName))
}

$added    = New-Object System.Collections.Generic.List[object]
$modified = New-Object System.Collections.Generic.List[object]
$same     = 0
$copied   = 0
$i        = 0

foreach ($nf in $newFiles) {
    $i++
    if (($i % 500) -eq 0) {
        Write-Progress -Activity "比對中" -Status "$i / $($newFiles.Count)" -PercentComplete (($i / $newFiles.Count) * 100)
    }

    $rel     = $nf.FullName.Substring($NewRoot.Length).TrimStart('\')
    $oldPath = Join-Path $OldRoot $rel

    $status = $null
    if (-not (Test-Path -LiteralPath $oldPath)) {
        $status = 'Added'
        $added.Add([pscustomobject]@{ Rel = $rel; Bytes = $nf.Length })
    }
    else {
        $oldItem = Get-Item -LiteralPath $oldPath -Force
        if (Test-SameFile $oldItem $nf) {
            $same++
            continue
        }
        $status = 'Modified'
        $modified.Add([pscustomobject]@{ Rel = $rel; Bytes = $nf.Length })
    }

    if (-not $ReportOnly) {
        $dest    = Join-Path $OutRoot $rel
        $destDir = Split-Path $dest -Parent
        if (-not (Test-Path -LiteralPath $destDir)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $nf.FullName -Destination $dest -Force
        $copied++
    }
}
Write-Progress -Activity "比對中" -Completed

# 找出「舊有、新沒有」的刪除檔(僅記錄)
Write-Host "掃描舊包(找刪除項)..."
$removed = New-Object System.Collections.Generic.List[object]
$oldFiles = Get-ChildItem -LiteralPath $OldRoot -Recurse -File -Force
foreach ($of in $oldFiles) {
    $rel     = $of.FullName.Substring($OldRoot.Length).TrimStart('\')
    $newPath = Join-Path $NewRoot $rel
    if (-not (Test-Path -LiteralPath $newPath)) {
        $removed.Add([pscustomobject]@{ Rel = $rel; Bytes = $of.Length })
    }
}

# 產出報表
if (-not $ReportOnly -and -not (Test-Path -LiteralPath $OutRoot)) {
    New-Item -ItemType Directory -Path $OutRoot -Force | Out-Null
}
$reportDir = if ($ReportOnly) { (Get-Location).Path } else { (Resolve-Path -LiteralPath $OutRoot).Path }
$reportCsv = Join-Path $reportDir 'update_manifest.csv'

$manifest = @()
$manifest += $added    | ForEach-Object { [pscustomobject]@{ Status = 'Added';    RelativePath = $_.Rel; Bytes = $_.Bytes } }
$manifest += $modified | ForEach-Object { [pscustomobject]@{ Status = 'Modified'; RelativePath = $_.Rel; Bytes = $_.Bytes } }
$manifest += $removed  | ForEach-Object { [pscustomobject]@{ Status = 'Removed';  RelativePath = $_.Rel; Bytes = $_.Bytes } }
$manifest | Export-Csv -LiteralPath $reportCsv -NoTypeInformation -Encoding UTF8

Write-Host ("=" * 60)
Write-Host ("新增 Added    : {0}" -f $added.Count)
Write-Host ("修改 Modified : {0}" -f $modified.Count)
Write-Host ("相同 Same     : {0}" -f $same)
Write-Host ("刪除 Removed  : {0} (僅記錄，未處理)" -f $removed.Count)
if (-not $ReportOnly) {
    Write-Host ("已複製到更新包 : {0} 個檔案 -> {1}" -f $copied, $reportDir)
}
Write-Host ("報表 : {0}" -f $reportCsv)
