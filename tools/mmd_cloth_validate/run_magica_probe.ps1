# Runs the Unity-side Magica Cloth probe headless (PlayMode tests Sdo.Tests.MmdClothProbe),
# producing magica_<scenario>.json here, then computes magica_metrics.json.
# The Unity editor must be CLOSED on the worktree project or the run aborts (lock).
# 路徑一律由這支腳本的位置推出——以前寫死成 H:/65_remake-mmd，
# feat/mmd-avatar 併進 main 之後那個 worktree 就不是要跑的那一個了。
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here "..\..")).Path
$proj = Join-Path $repo "65\My project"
$unity = "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe"

$lock = Join-Path $proj "Temp/UnityLockfile"
if (Test-Path $lock) {
    try { $fs = [System.IO.File]::Open($lock, 'Open', 'ReadWrite', 'None'); $fs.Close() }
    catch { Write-Error "Unity editor has the project open (lock held) - close it first."; exit 1 }
}

& $unity -batchmode -projectPath $proj -runTests -testPlatform PlayMode `
    -testFilter "Sdo.Tests.MmdClothProbe" `
    -testResults (Join-Path $here "playmode_results.xml") `
    -logFile (Join-Path $here "unity_probe.log") | Out-Null
Write-Host "Unity exit code: $LASTEXITCODE (0 = all tests passed)"

python (Join-Path $here "compute_metrics_magica.py") magica
exit $LASTEXITCODE
