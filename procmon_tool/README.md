# ProcMon 音效擷取工具（SDO 場景音效調查）

用 Process Monitor 實機擷取 `sdo_stand_alone.exe` 開了哪些音檔（.wav/.ogg），
用來一翻兩瞪眼確認「某場景到底有沒有播某個 SE / 是哪個檔在當環境音」。

## 需求
- **以系統管理員開啟 PowerShell**（ProcMon 要載核心驅動）。
- ProcMon 已裝：`winget install Microsoft.Sysinternals.ProcessMonitor`
- Python 套件：`pip install procmon-parser`

## 用法
在**管理員 PowerShell**裡：

```powershell
cd h:\65_remake\procmon_tool

# 遊戲已經開著 → 開始錄，進場景，回來按 Enter 看報告
.\capture.ps1

# 順便幫你啟動遊戲
.\capture.ps1 -Launch

# 想確認別的檔（預設是 SE_0031.wav）
.\capture.ps1 -Highlight SE_0030.wav
```

流程：腳本開始擷取 → 你**從別的地方「切換進」要測的場景**（一定要 fresh 進場，
環境音是進場那一刻開檔、之後在記憶體循環）→ 停留幾秒 → 回到視窗按 **Enter** →
自動停止並印出報告。

每次擷取存到 `runs\cap_<時間>.pml`，可事後重新分析：
```powershell
python .\pm_analyze.py runs\cap_20260620_213700.pml SE_0031.wav
```

## 報告看什麼
- **UNIQUE sound files opened**：這次遊戲實際開過的音檔（依時間排序）。
  `OK` = 成功讀取、`NOT_FOUND` = 探測但檔不在（走封裝檔或缺檔）。
- **HIGHLIGHT**：指定檔有沒有被開（`*** PLAYED ***` / `NOT opened`）。
- **scene/stage asset access**：場景資產載入的時間點，用來對齊「進場那一刻」。

## 已知結論（2026-06-20 實測）
進海底場景（SCN0014，已確認載入 `14_HAIDI` 資產）時：
- `SE_0031.wav` **開檔 0 次** → 不是場景環境音。
- 實際在播的是 `BMG_002.ogg` / `BMG_013.ogg`（BGM 隨機池循環）。
→ 海底「環境氛圍」其實是 BGM，不是專屬 SE。

## 檔案
- `capture.ps1` — 主流程（開始/停止/分析）
- `pm_analyze.py` — 解析 .pml，列出開過的音檔
- `make_filter.py` — 產生只抓目標行程的 ProcMon 過濾器（`pm_filter.pmc`）
- `runs/` — 每次擷取的 .pml（已 gitignore）
