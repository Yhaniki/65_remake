# 為這個 worktree 組一棵「線上優先」的 DATA 資料樹 (全部用 junction / hardlink，不複製大檔)。
#
# 為什麼: 商城的 2D 道具 (ITEM2D / DAOJU / PETAVATAR / 完整 UI\SHOP art) 只存在**線上**客戶端資料樹
#   assets\閉撰敃氪\DatasSDO —— 離線 sdox_offline\Extracted 根本沒有這些資料夾。所以這個 worktree 的
#   data root 指向線上樹 (SdoExtracted 的 data_root.txt 覆寫機制)。
#
# 合併規則 (每一條在 docs/SHOP_2D_DATA.md 留檔，供未來抽「乾淨資料包」用):
#   * DatasSDO 的每個頂層資料夾 -> junction (線上優先，資料較全)
#   * UI/ 做「深一層」合併: 每個 UI 子資料夾各自 junction，線上有的用線上、線上沒有的補離線
#     (離線獨有的 UI 子資料夾才不會消失)
#   * 離線 Extracted 獨有的頂層資料夾 -> junction 補上 (目前只有 PROFILE，且它要可寫 -> 實體複製)
#   * SE   -> 線上 (356 檔，離線 87 檔的嚴格超集)
#   * MUSIC-> 離線 sdox_offline\music (匯入的 1516 首新歌在這裡，線上樹沒有)
#   * BGM  -> 離線 Extracted\UI\BGM (大廳/房間隨機播放清單)
#   * iteminfo.dat / setinfo.dat -> 線上客戶端根目錄，hardlink 到 DATA 根 (AvatarItemCatalog 第一順位就找這裡)
#
# 用法:  pwsh -File tools\link_data_root.ps1            (預設組在 <worktree>\DATA 並寫 data_root.txt)
#        pwsh -File tools\link_data_root.ps1 -Force     (重建)
[CmdletBinding()]
param(
    [string]$Repo    = (Split-Path -Parent $PSScriptRoot),
    [string]$Assets  = 'H:\65_remake\assets',
    [string]$Out     = $null,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path $Repo 'DATA' }

$online   = Join-Path $Assets '閉撰敃氪'
$onData   = Join-Path $online 'DatasSDO'
$offline  = Join-Path $Assets 'sdox_offline'
$offData  = Join-Path $offline 'Extracted'

foreach ($p in @($onData, $offData)) {
    if (-not (Test-Path $p)) { throw "找不到資料樹: $p" }
}

if ((Test-Path $Out) -and $Force) { Remove-Item $Out -Recurse -Force }
if (-not (Test-Path $Out)) { New-Item -ItemType Directory -Path $Out | Out-Null }

$manifest = New-Object System.Collections.ArrayList

function Link-Dir([string]$name, [string]$target, [string]$why) {
    $dest = Join-Path $Out $name
    if (Test-Path $dest) { return }
    if (-not (Test-Path $target)) { Write-Warning "  skip $name (來源不存在: $target)"; return }
    New-Item -ItemType Junction -Path $dest -Target $target | Out-Null
    [void]$manifest.Add([pscustomobject]@{ Entry = $name; Kind = 'junction'; Source = $target; Why = $why })
}

function Link-File([string]$name, [string]$target, [string]$why) {
    $dest = Join-Path $Out $name
    if (Test-Path $dest) { return }
    if (-not (Test-Path $target)) { Write-Warning "  skip $name (來源不存在: $target)"; return }
    New-Item -ItemType HardLink -Path $dest -Target $target | Out-Null
    [void]$manifest.Add([pscustomobject]@{ Entry = $name; Kind = 'hardlink'; Source = $target; Why = $why })
}

Write-Output "[link] 線上 DatasSDO 頂層資料夾 -> junction"
foreach ($d in Get-ChildItem $onData -Directory) {
    if ($d.Name -eq 'UI') { continue }   # UI 走深一層合併
    Link-Dir $d.Name $d.FullName 'online DatasSDO (fuller)'
}

Write-Output "[link] 線上 DatasSDO 頂層散檔 -> hardlink"
foreach ($f in Get-ChildItem $onData -File) { Link-File $f.Name $f.FullName 'online DatasSDO (fuller)' }

Write-Output "[link] UI 深一層合併 (線上優先，離線補缺)"
$uiOut = Join-Path $Out 'UI'
if (-not (Test-Path $uiOut)) { New-Item -ItemType Directory -Path $uiOut | Out-Null }
foreach ($d in Get-ChildItem (Join-Path $onData 'UI') -Directory) {
    Link-Dir "UI\$($d.Name)" $d.FullName 'online UI'
}
$offUi = Join-Path $offData 'UI'
if (Test-Path $offUi) {
    foreach ($d in Get-ChildItem $offUi -Directory) {
        if (-not (Test-Path (Join-Path $uiOut $d.Name))) { Link-Dir "UI\$($d.Name)" $d.FullName 'offline-only UI folder' }
    }
    foreach ($f in Get-ChildItem $offUi -File) { Link-File "UI\$($f.Name)" $f.FullName 'offline UI loose file' }
}
foreach ($f in Get-ChildItem (Join-Path $onData 'UI') -File) { Link-File "UI\$($f.Name)" $f.FullName 'online UI loose file' }

Write-Output "[link] 離線獨有的頂層資料夾 -> junction (PROFILE 除外: 要可寫)"
foreach ($d in Get-ChildItem $offData -Directory) {
    if ($d.Name -eq 'PROFILE' -or $d.Name -eq 'UI') { continue }
    if (-not (Test-Path (Join-Path $Out $d.Name))) { Link-Dir $d.Name $d.FullName 'offline-only folder' }
}

# PROFILE: 玩家存檔會寫進去 -> 實體複製一份 (不共用主 worktree 的存檔)
$profOut = Join-Path $Out 'PROFILE'
if (-not (Test-Path $profOut)) {
    Copy-Item (Join-Path $offData 'PROFILE') $profOut -Recurse
    [void]$manifest.Add([pscustomobject]@{ Entry = 'PROFILE'; Kind = 'copy(writable)'; Source = (Join-Path $offData 'PROFILE'); Why = '玩家存檔要可寫，不能共用' })
}

Write-Output "[link] 音效 / 音樂"
Link-Dir 'SE'    (Join-Path $online 'SE')                 'online SE (356 檔，離線 87 的超集)'
Link-Dir 'MUSIC' (Join-Path $offline 'music')             'offline music (匯入的新歌在這裡)'
Link-Dir 'BGM'   (Join-Path $offData 'UI\BGM')            'offline 大廳/房間 BGM'

Write-Output "[link] 商城目錄 (iteminfo/setinfo)"
Link-File 'iteminfo.dat' (Join-Path $online 'iteminfo.dat') '商城道具目錄 (31,563 筆)'
Link-File 'setinfo.dat'  (Join-Path $online 'setinfo.dat')  '套裝組件表'

# data_root.txt: SdoExtracted 的最高優先覆寫 (見 SdoExtracted.ConfiguredRoot)。已在 .gitignore。
Set-Content -Path (Join-Path $Repo 'data_root.txt') -Value $Out -Encoding utf8 -NoNewline
Write-Output "[link] data_root.txt -> $Out"

$manifest | Export-Csv -Path (Join-Path $Repo 'DATA_MANIFEST.csv') -NoTypeInformation -Encoding utf8
Write-Output "[link] 完成: $($manifest.Count) 個掛載點 (清單: DATA_MANIFEST.csv)"
