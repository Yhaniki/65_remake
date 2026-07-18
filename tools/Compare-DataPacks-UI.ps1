<#
    DATA 包差異比對工具 (拖曳版 GUI)
    - 把「新的 DATA 資料夾」和「舊的 DATA 資料夾」拖進對應欄位(或按瀏覽)
    - 按「開始比對」，會把新增+修改的檔案依原結構複製到輸出資料夾當更新包
    - 需以 STA 執行，請用 Compare-DataPacks-UI.cmd 啟動(雙擊即可)
#>

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# ---------------------------------------------------------------------------
# 背景比對工作(在獨立 runspace 執行，避免 UI 卡住)
# ---------------------------------------------------------------------------
$script:worker = {
    param($sync, $OldRoot, $NewRoot, $OutRoot, $Fast, $ReportOnly, $ClearOut)

    function Log($m) { $sync.Log.Enqueue($m) }

    try {
        $OldRoot = $OldRoot.TrimEnd('\')
        $NewRoot = $NewRoot.TrimEnd('\')

        Log("舊包 : $OldRoot")
        Log("新包 : $NewRoot")
        Log("輸出 : $OutRoot")
        Log("比對 : " + $(if ($Fast) { '大小 + 修改時間 (快速)' } else { '大小 + SHA256 內容雜湊' }))
        Log("模式 : " + $(if ($ReportOnly) { '只出清單，不複製' } else { '複製新增/修改檔案' }))
        Log(("-" * 56))

        if (-not $ReportOnly -and $ClearOut -and (Test-Path -LiteralPath $OutRoot)) {
            Log('清空輸出資料夾...')
            Get-ChildItem -LiteralPath $OutRoot -Force | Remove-Item -Recurse -Force
        }

        Log('掃描新包...')
        $newFiles = Get-ChildItem -LiteralPath $NewRoot -Recurse -File -Force
        $total = $newFiles.Count
        $sync.Total = $total
        Log("新包檔案數 : $total")

        $added = 0; $modified = 0; $same = 0; $copied = 0; $i = 0
        $manifest = New-Object System.Collections.Generic.List[object]

        foreach ($nf in $newFiles) {
            $i++
            if (($i % 100) -eq 0 -and $total -gt 0) {
                $sync.Progress = [int](($i / $total) * 100)
                $sync.Status = "比對中 $i / $total"
            }

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
                        $h1 = (Get-FileHash -LiteralPath $oldItem.FullName -Algorithm SHA256).Hash
                        $h2 = (Get-FileHash -LiteralPath $nf.FullName -Algorithm SHA256).Hash
                        $sameFile = ($h1 -eq $h2)
                    }
                }
                if ($sameFile) { $same++; continue }
                $status = 'Modified'; $modified++
            }

            $manifest.Add([pscustomobject]@{ Status = $status; RelativePath = $rel; Bytes = $nf.Length })

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
        $sync.Progress = 100

        Log('掃描舊包(找刪除項)...')
        $removed = 0
        $oldFiles = Get-ChildItem -LiteralPath $OldRoot -Recurse -File -Force
        foreach ($of in $oldFiles) {
            $rel = $of.FullName.Substring($OldRoot.Length).TrimStart('\')
            if (-not (Test-Path -LiteralPath (Join-Path $NewRoot $rel))) {
                $manifest.Add([pscustomobject]@{ Status = 'Removed'; RelativePath = $rel; Bytes = $of.Length })
                $removed++
            }
        }

        if (-not (Test-Path -LiteralPath $OutRoot)) {
            New-Item -ItemType Directory -Path $OutRoot -Force | Out-Null
        }
        $reportDir = (Resolve-Path -LiteralPath $OutRoot).Path
        $reportCsv = Join-Path $reportDir 'update_manifest.csv'
        $manifest | Export-Csv -LiteralPath $reportCsv -NoTypeInformation -Encoding UTF8

        Log(("=" * 56))
        Log("新增 Added    : $added")
        Log("修改 Modified : $modified")
        Log("相同 Same     : $same")
        Log("刪除 Removed  : $removed (僅記錄，未處理)")
        if (-not $ReportOnly) { Log("已複製 $copied 個檔案到 : $reportDir") }
        else { Log('(只出清單模式，未複製任何檔案)') }
        Log("報表 : $reportCsv")
        Log('完成 OK')
        $sync.Summary = "新增 $added、修改 $modified、相同 $same、刪除 $removed"
    }
    catch {
        $sync.Error = $_.Exception.Message
        Log("錯誤 : $($_.Exception.Message)")
    }
    finally {
        $sync.Done = $true
    }
}

# ---------------------------------------------------------------------------
# 介面
# ---------------------------------------------------------------------------
$form = New-Object System.Windows.Forms.Form
$form.Text = 'DATA 包差異比對工具'
$form.ClientSize = New-Object System.Drawing.Size(680, 560)
$form.MinimumSize = New-Object System.Drawing.Size(560, 420)
$form.StartPosition = 'CenterScreen'
try { $form.Font = New-Object System.Drawing.Font('Microsoft JhengHei UI', 9) } catch {}

function New-Label($text, $x, $y) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text; $l.AutoSize = $true
    $l.Location = New-Object System.Drawing.Point($x, $y)
    $form.Controls.Add($l); return $l
}
function New-PathBox($x, $y, $w) {
    $t = New-Object System.Windows.Forms.TextBox
    $t.Location = New-Object System.Drawing.Point($x, $y)
    $t.Size = New-Object System.Drawing.Size($w, 24)
    $t.Anchor = 'Top,Left,Right'
    $t.AllowDrop = $true
    $t.Add_DragEnter({ param($s, $e)
        if ($e.Data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) {
            $e.Effect = [System.Windows.Forms.DragDropEffects]::Copy
        }
    })
    $t.Add_DragDrop({ param($s, $e)
        $paths = $e.Data.GetData([System.Windows.Forms.DataFormats]::FileDrop)
        if ($paths -and $paths.Count -gt 0) {
            $p = $paths[0]
            if (Test-Path -LiteralPath $p -PathType Leaf) { $p = Split-Path $p -Parent }
            $s.Text = $p
        }
    })
    $form.Controls.Add($t); return $t
}
function New-BrowseBtn($x, $y, $target) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = '瀏覽...'
    $b.Location = New-Object System.Drawing.Point($x, $y)
    $b.Size = New-Object System.Drawing.Size(100, 26)
    $b.Anchor = 'Top,Right'
    $b.Add_Click({
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        if ($target.Text -and (Test-Path -LiteralPath $target.Text)) { $dlg.SelectedPath = $target.Text }
        if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $target.Text = $dlg.SelectedPath }
    }.GetNewClosure())
    $form.Controls.Add($b); return $b
}

New-Label '新的 DATA 包（有更新的、當比對基準）：' 12 12 | Out-Null
$txtNew = New-PathBox 12 34 536
$btnBrowseNew = New-BrowseBtn 556 33 $txtNew

New-Label '舊的 DATA 包（拿來比對的）：' 12 70 | Out-Null
$txtOld = New-PathBox 12 92 536
$btnBrowseOld = New-BrowseBtn 556 91 $txtOld

New-Label '更新包輸出資料夾（複製到這裡）：' 12 128 | Out-Null
$txtOut = New-PathBox 12 150 536
$btnBrowseOut = New-BrowseBtn 556 149 $txtOut

# 預填題主給的路徑
$txtNew.Text = 'H:\65_remake_clean\DATA'
$txtOld.Text = 'H:\65_remake\Build\Dance\DATA'
$txtOut.Text = 'H:\65_remake_clean\DATA_update'

$chkFast = New-Object System.Windows.Forms.CheckBox
$chkFast.Text = '快速模式(只比大小+時間)'; $chkFast.AutoSize = $true
$chkFast.Location = New-Object System.Drawing.Point(12, 186)
$form.Controls.Add($chkFast)

$chkReport = New-Object System.Windows.Forms.CheckBox
$chkReport.Text = '只出清單不複製'; $chkReport.AutoSize = $true
$chkReport.Location = New-Object System.Drawing.Point(220, 186)
$form.Controls.Add($chkReport)

$chkClear = New-Object System.Windows.Forms.CheckBox
$chkClear.Text = '先清空輸出資料夾'; $chkClear.AutoSize = $true
$chkClear.Location = New-Object System.Drawing.Point(360, 186)
$form.Controls.Add($chkClear)

$btnStart = New-Object System.Windows.Forms.Button
$btnStart.Text = '開始比對'
$btnStart.Location = New-Object System.Drawing.Point(12, 214)
$btnStart.Size = New-Object System.Drawing.Size(150, 30)
$form.Controls.Add($btnStart)

$pb = New-Object System.Windows.Forms.ProgressBar
$pb.Location = New-Object System.Drawing.Point(172, 217)
$pb.Size = New-Object System.Drawing.Size(492, 22)
$pb.Anchor = 'Top,Left,Right'
$pb.Minimum = 0; $pb.Maximum = 100; $pb.Style = 'Continuous'
$form.Controls.Add($pb)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Text = '就緒'; $lblStatus.AutoSize = $true
$lblStatus.Location = New-Object System.Drawing.Point(12, 250)
$form.Controls.Add($lblStatus)

$txtLog = New-Object System.Windows.Forms.TextBox
$txtLog.Location = New-Object System.Drawing.Point(12, 274)
$txtLog.Size = New-Object System.Drawing.Size(652, 274)
$txtLog.Anchor = 'Top,Bottom,Left,Right'
$txtLog.Multiline = $true
$txtLog.ScrollBars = 'Vertical'
$txtLog.ReadOnly = $true
$txtLog.BackColor = [System.Drawing.Color]::White
$txtLog.Font = New-Object System.Drawing.Font('Consolas', 9)
$form.Controls.Add($txtLog)

# ---------------------------------------------------------------------------
# 進度輪詢 timer(UI 執行緒)
# ---------------------------------------------------------------------------
$script:timer = New-Object System.Windows.Forms.Timer
$script:timer.Interval = 150
$script:timer.Add_Tick({
    if ($script:sync) {
        while ($script:sync.Log.Count -gt 0) {
            $txtLog.AppendText([string]$script:sync.Log.Dequeue() + "`r`n")
        }
        $p = [int]$script:sync.Progress
        if ($p -ge 0 -and $p -le 100) { $pb.Value = $p }
        if ($script:sync.Status) { $lblStatus.Text = $script:sync.Status }

        if ($script:sync.Done) {
            $script:timer.Stop()
            try { $script:ps.EndInvoke($script:handle) } catch {}
            try { $script:ps.Runspace.Close(); $script:ps.Dispose() } catch {}
            $btnStart.Enabled = $true
            $txtNew.Enabled = $true; $txtOld.Enabled = $true; $txtOut.Enabled = $true
            if ($script:sync.Error) { $lblStatus.Text = '發生錯誤：' + $script:sync.Error }
            else { $lblStatus.Text = '完成 — ' + $script:sync.Summary }
        }
    }
})

# ---------------------------------------------------------------------------
# 開始比對
# ---------------------------------------------------------------------------
$btnStart.Add_Click({
    $new = $txtNew.Text.Trim(); $old = $txtOld.Text.Trim(); $out = $txtOut.Text.Trim()

    if (-not (Test-Path -LiteralPath $new -PathType Container)) {
        [System.Windows.Forms.MessageBox]::Show('新包路徑不存在或不是資料夾：' + $new, '錯誤'); return
    }
    if (-not (Test-Path -LiteralPath $old -PathType Container)) {
        [System.Windows.Forms.MessageBox]::Show('舊包路徑不存在或不是資料夾：' + $old, '錯誤'); return
    }
    if ([string]::IsNullOrWhiteSpace($out)) {
        [System.Windows.Forms.MessageBox]::Show('請指定輸出資料夾', '錯誤'); return
    }

    $newFull = [System.IO.Path]::GetFullPath($new).TrimEnd('\') + '\'
    $oldFull = [System.IO.Path]::GetFullPath($old).TrimEnd('\') + '\'
    $outFull = [System.IO.Path]::GetFullPath($out).TrimEnd('\') + '\'
    if ($outFull.StartsWith($newFull, [System.StringComparison]::OrdinalIgnoreCase) -or
        $outFull.StartsWith($oldFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        [System.Windows.Forms.MessageBox]::Show('輸出資料夾不可以放在新包或舊包裡面，請改別的位置。', '錯誤'); return
    }

    $txtLog.Clear(); $pb.Value = 0; $lblStatus.Text = '執行中...'
    $btnStart.Enabled = $false
    $txtNew.Enabled = $false; $txtOld.Enabled = $false; $txtOut.Enabled = $false

    $script:sync = [hashtable]::Synchronized(@{})
    $script:sync.Log = [System.Collections.Queue]::Synchronized((New-Object System.Collections.Queue))
    $script:sync.Progress = 0
    $script:sync.Done = $false
    $script:sync.Error = $null
    $script:sync.Status = '開始...'
    $script:sync.Summary = ''
    $script:sync.Total = 0

    $script:ps = [powershell]::Create()
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $script:ps.Runspace = $rs
    [void]$script:ps.AddScript($script:worker).
        AddArgument($script:sync).
        AddArgument($old).
        AddArgument($new).
        AddArgument($out).
        AddArgument([bool]$chkFast.Checked).
        AddArgument([bool]$chkReport.Checked).
        AddArgument([bool]$chkClear.Checked)
    $script:handle = $script:ps.BeginInvoke()
    $script:timer.Start()
})

[void]$form.ShowDialog()
$form.Dispose()
