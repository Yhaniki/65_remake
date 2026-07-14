# 譜面編輯器（Chart Editor）

參考 `H:\bms\tools\stepfile_dump2.py`（.gn 圖示化預覽 + 編輯器）做的**遊戲內**版本。
第一版＝**唯讀預覽**：純黑背景的音符板 + 音樂波形 + 自由 seek + 在裡面換歌。

## 進入方式

| 環境 | 做法 |
|---|---|
| Unity 編輯器 | `Tools ▸ SDO ▸ Boot Into Chart Editor (譜面編輯器)` → 按 Play |
| 指定某一首 | `Tools ▸ SDO ▸ Chart Editor: choose song (.gn)…` → 輸入 `sdom1197k.gn` |
| dance.exe | 環境變數 `SDO_EDITOR=1`（或 `SDO_EDITOR=sdom1197k.gn`） |

（機制同既有的 `SDO_SCENE` / `SDO_ROOM` / `SDO_SHOP`：`ScreenGameplay.DevVar` 先讀 env、編輯器再讀 EditorPrefs。
設了 `SDO_EDITOR` → `FrontendApp.Boot()` 直接不啟動前端，`ChartEditorScreen` 自己開起來。）

開哪一首：**上次開的那首**（記在 PlayerPrefs）；沒有紀錄（或那首檔案不見了）就開**編號最大的那首**
——也就是最後匯入的新歌。條件是「有譜」而且「DATA 樹裡真的有檔案」：目錄有 4346 筆，但有些歌的檔案不在這棵樹裡
（例如第一筆 `sdom0.gn` 新手教学），只看目錄會開出一片空白。

## 操作

| 鍵 | 作用 |
|---|---|
| `空白` / `\` | 播放 / 暫停 |
| `↑` `↓` | 前後一格（格線的細分） |
| `PgUp` `PgDn` | 前後一小節 |
| `←` `→` | 格線細分（每拍 1 / 2 / 3 / 4 / 6 / 8 / 12 / 16 格） |
| `Home` `End` | 跳到開頭 / 結尾 |
| `F1` | 歌單（可搜尋，點一下就換歌） |
| `Tab` | 換難度（該首沒有的難度會跳過） |
| `F3` / `G` | 波形 / 格線 開關 |
| `F5` `F6` | 音符下落速度檔位（＝房間的「速度」） |
| `[` `]` `=` | 遊戲流速（音樂+音符一起變速 / 回 1×） |
| `F7` | 打拍音（assist tick）—— 對拍很好用 |

上方 IMGUI 工具列另有：難度按鈕、進度條（拖曳＝seek，暫停中也可以）、小節/拍讀數、BPM、音符數。

## 怎麼做的

### 畫面 = ScreenGameplay 的 `editorMode`

沒有另外寫一套音符渲染 —— 整個遊玩畫面照常建起來（音符板、受擊線、note 皮、
[捲動/變速數學](../architecture/scroll-timing.md) 全部沿用），只是打開 `ScreenGameplay.editorMode` 之後：

* **不載 3D 場景、不載舞者** → 沒有 SceneCam / 背景 quad → 主相機本來就是 `SolidColor` 黑（`SdoLayout.SetupCamera`）＝純黑背景；
* 不判定、不扣血、不計分、不結算，也沒有 READY/GO 開場（直接停在 0ms 等你按播放）；
* HP 條 / 分數 / 名次 / 歌曲資訊列全部收起來，只留音符板；
* 時間可以自由 seek。

程式在 `ScreenGameplay.Editor.cs`（partial），核心那支只多了幾個 `if (editorMode)` 分歧點。

### seek 的四個錨點

改時間必須**四件事一起搬**，少一個就音畫不同步（原因見 `Sdo.Ruleset.GameRate` 的註解）：

1. `_songStartDspTime` —— 音訊排程用的 dsp 錨點（`GameRate.AnchorForChartSeconds`）
2. `_clockStart` —— 譜面時鐘的 wall 基準（暫停時 `Time.timeScale = 0` → `timeAsDouble` 凍結 → 時鐘自然停住）
3. `AudioSource` 的播放位置（`.time`；還在無聲數拍裡就改用 `PlayScheduled`）
4. `GameplayClock` —— 平滑器要 `Reset()` 重新 seed

`EditorSeekMs()` 就是把這四件事做完。**注意**：編輯器的暫停/變速要走 `EditorSetPaused` / `EditorSetRate`，
不能用原本的 `SetPaused` / `SetGameRate` —— 後者的恢復路徑假設音源是 `Pause()` 過的，但 seek 是 `Stop()` → `Play()`，
直接用會恢復不了聲音；`SetGameRate` 在暫停中還會拿一直在跑的 dsp 去反推「現在的譜面時間」，算出來是錯的。

### 波形

* `Sdo.Osu.WaveformPeaks`（純邏輯）：PCM → 每 20ms 一格的**峰值**（不是 RMS —— 編譜要看「鼓點在哪」，
  RMS 會把單一下打點平均掉），最後整體正規化。可分批餵：一首 4 分鐘立體聲有 ~2100 萬個取樣，
  一次算完會卡一大幀，所以 `ChartEditorScreen.BuildPeaksCo` 每幀讀一塊 `AudioClip.GetData(buf, offset)`。
* 畫在音符板右邊、跟著捲動：`ChartEditorOverlay` 每幀重建一個 Mesh（只畫看得到的那段，幾十～幾百個四邊形）。
* **時間→Y 完全走 `ScreenGameplay.EditorYForTime`（＝音符自己用的那條 `YForTime`）**，所以波形、格線、音符
  永遠在同一格上：變速（type-1）、掉落方向（向上/向下）、速度檔位改了都自動跟著。
* 波形的第 0 格對到**音樂真正開始的譜面時間**（type-10 音樂起點的無聲數拍，見
  [`sdo-music-start-type10-marker`](../reverse-engineering/SM_GN_NOTE_FORMAT.md)），不是譜面第 0 拍。

### 格線

`Sdo.Osu.BeatGrid`（純邏輯）：由 `OsuBeatmap.TimingPoints`（＝ GnChart 算出來的分段 BPM）接回一條分段線性的
拍↔毫秒時間軸。`.gn` 是 4/4，一個 measurement = 4 拍，所以小節線就是每 4 拍一條。

## 測試

* `BeatGridTests` / `WaveformPeaksTests`（EditMode，純邏輯）
* `ChartEditorTest`（PlayMode，端對端）：真的把編輯器開起來，驗「沒有場景/舞者、背景是黑的、音符讀進來、
  seek 到某顆音符的時間時該音符正好落在受擊線上、波形真的從 PCM 解出來且時間原點對到音樂起點」，
  並存一張 `chart-editor-capture.png` 供人眼複核。

## 還沒做：編輯與存檔

第一版只讀不寫。要加編輯（放/刪 tap、拖 hold、增刪小節、複製貼上、Ctrl+Z）時：

* 現在的 `GnChart` 是**單向**的（.gn → `OsuBeatmap`，frame 結構丟掉了）。編輯需要一個 frame-level 的模型
  （measurement / step_frame_type / interval / slots），對照 `bms_sdo/gn_master_core.py` 的 `StepFileBin` 與
  `bms_sdo/gn_editor.py` 的那組原語（`set_note` / `toggle_tap` / `place_hold` / `insert_measure` / `paste_region`，
  全都是「最小變動」：只重建被改到的 (measurement, lane) frame，其餘位元不動）。
* 存檔已定案：**以原加密覆蓋原檔，覆蓋前自動備份 `<name>.gn.bak`**。加密本身很單純（LCG `state *= 0x3D09`，
  加密就是把減改成加），對照 `bms_sdo/gn_crypto.py`：
  * `sdom`（3170 首）：檔首明文資源表 + 內嵌 StepFile，body 用 LCG 加密（seed 在 `gn_keytable.json` 裡）；
    原版 exe 會看大小/CRC，所以要照 `repack_sdom_gn_template` 的做法保大小並修 CRC32（末 4 bytes meet-in-the-middle）。
    重製版自己的 `GnChart.Decrypt` **不驗 CRC 也不驗大小**，所以就算只給重製版用，寫回也不難。
  * `rewu`（1172 首）：整檔 LCG。`ddrm`（4 首）：DDRM 容器（seed 在檔頭）。`plain`：直接寫。
