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
| **`Ctrl`+`↑` `↓`** | **顯示縮放：區域變寬 / 變窄**（＝ StepMania `ScreenEdit.cpp` 的 Ctrl+Up/Down 改 scroll speed，步進 0.5、底端 0.25） |
| **`F11` `F12`** | **單首 offset −/＋ 20ms**（按住 `Alt` 為 ±1ms）—— 同 StepMania 的 0.02 / 0.001 秒。**動的是音樂，音符不動** |
| `PgUp` `PgDn` | 前後一小節 |
| `←` `→` | 格線細分（每拍 1 / 2 / 3 / 4 / 6 / 8 / 12 / 16 格） |
| `Home` `End` | 跳到開頭 / 結尾 |
| `F1` | 歌單（可搜尋，點一下就換歌） |
| `Tab` | 換難度（該首沒有的難度會跳過） |
| `F3` / `G` | 波形 / 格線 開關 |
| `F5` `F6` | 音符下落速度檔位（＝房間的「速度」） |
| `[` `]` `=` | 遊戲流速（音樂+音符一起變速 / 回 1×） |
| `F7` | 打拍音（assist tick）—— 對拍很好用 |
| `F2` | **打拍測試（校時）**——見下 |

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

**顏色 = StepMania 的 note quantization**（`BeatGrid.SnapOf`：一小節 192 row，看位置能被誰整除）：

| 4分 | 8分 | 12分 | 16分 | 24分 | 32分 | 更細 |
|---|---|---|---|---|---|---|
| 紅 | 藍 | 深紫 | 黃 | 粉紅 | 橘 | 白 |

粗細另外表示層級：小節線 3px、拍線 2px、其餘 1px。

### 兩種 offset（很容易搞混，方向不一樣）

| | 動什麼 | 存在哪 | 幹嘛用的 |
|---|---|---|---|
| **全域** `globalOffsetMs` | **譜面時鐘**（音符與判定一起位移，音樂不動） | `config.ini`（打拍測試面板可存） | 個人偏好／跨機微調（機器延遲已自動補掉，見下） |
| **單首** `offsetMs` | **音樂**（音符/判定線一格都不動，只有音樂前後挪；波形跟著音樂走） | `StreamingAssets/song_name_overrides.json`，**手改** | 補「這首譜跟音檔沒對齊」 |

單首 offset 用 **F11/F12** 邊聽邊調（一次 20ms，Alt 微調 1ms），但**不會自動寫檔** —— 調到滿意後，面板會印出一行
可以直接貼進 `song_name_overrides.json` 的 JSON，自己寫進去。key 是 gn 詞幹（`sdom0001`），k/t 兩份譜共用同一筆
（同一個音檔，本來就該共用）。

> 為什麼放在 `song_name_overrides.json`：它是**唯一**一份手改的歌曲資料。`song_catalog.json` 是工具從 .gn 重建的
> （bpm／難度／音符數以實際譜面為準，重掃會蓋掉），所以任何「人決定的東西」都只能住在 overrides。
> `build_song_name_overrides.py` 全量重寫時會**一律保留** `offsetMs`（連 `--reseed` 也不動它）。

### 音訊延遲：兩段自動補償 ＋ 一個要校的殘差

延遲**大部分是自動補掉的**，`globalOffsetMs` 只負責收尾。

**① 輸出延遲 → 譜面時鐘自動扣掉（＝ StepMania 的「時鐘讀播放游標」）**

StepMania 的歌曲時鐘不是「我送了多少音訊出去」，而是去問音效卡「**現在正從喇叭出來的是第幾個取樣**」
（DirectSound 播放游標 → `RageSound::GetPositionSecondsInternal` → `pos_map.Search` → `m_fMusicSeconds`）。
緩衝區裡那段「已混音、還沒出喇叭」的音訊從來不算進時鐘 —— 輸出延遲在判定路徑上**自動抵銷**。這就是它敢把
Windows DSound 的 writeahead 開到 8192 frames（186ms）而 `GlobalOffsetSeconds` 預設 0 的原因；
`GetPlayLatency()` 只拿去提前排程打拍音，從不進判定。

Unity 沒有播放游標 API（`AudioSettings.dspTime` 是**混音**游標），但它領先喇叭的距離可以算：
`bufferLength × numBuffers / sampleRate`（`AudioSettings.GetDSPBufferSize`）。而
`ChartSecondsFromDsp = rate×(dsp − anchor) + countIn`，所以 **dsp 退 L 秒 ⇔ 譜面時間退 rate×L 秒** ——
一個常數，直接疊在 `GameplayClock.OffsetMs` 上（`ScreenGameplay.ApplyClockOffset`），不必動任何錨點。

> **不變式：排程（`PlayScheduled`）一律走原始 `dspTime`，只有「讀時鐘」走播放游標。** 兩邊一起搬就抵銷了。

**② 打拍音的前導靜音 → 排程自動提早（音檔不動）**

`PlayScheduled` 排的是 **clip 的第 0 個取樣**，不是「聽得到的那一刻」。官方 `assist_tick.ogg` 前面有 ~30ms 空白
（實測 44.1kHz、全長 81.4ms：前 26ms 全 0，起音 29.8ms、峰值 34.3ms）→ 每一聲 click 都晚 30ms 進耳朵。
`ScreenGameplay.MeasureOnsetSec` 載入時量出來，`TickAssist` 排程時減掉（同 StepMania：提早排程、從不改音檔）。

這種延遲特別惡毒：它**只污染耳朵、不污染眼睛** —— 音符的畫面位置是譜面時鐘畫出來的，所以「看著 note 打」
量不到它，「聽著節拍器打」卻會白白多吃 30ms。兩個測試因此對不起來，整包還會被誤認成音效卡延遲。

倒推（`TickAssist` 的註解有完整推導）：譜面時間 T 的 click 要在「時鐘讀到 T」那一刻進耳朵
⇒ **排在 `Draw(T) − onset`**。輸出延遲 L 自己消掉了（它對時鐘和聲音一視同仁），只在「排得到的界線」
`T > now + (L + onset)×rate`（＝ `TickLeadChartMs`）裡還留著。

**③ 驅動延遲 → 寫死的平台常數 `ScreenGameplay.DriverLatencyMs = 33`**

補完①，剩下的是 **FMOD 底下（WASAPI/驅動/喇叭）Unity 看不到的那一段**。沒有 API 問得到，只能實測。
用打拍測試在兩種 buffer 下各量一次（聽節拍器打 100 下取中位數）：

| DSP buffer | ① 算得到的 | 聽覺中位數 | 視覺中位數 | 殘差 ＝ ③ |
|---|---|---|---|---|
| 1024×4 @48k | 85.3 ms | +32.8 | +1.6 | **31.2 ms** |
| 512×4 @48k | 42.7 ms | +33.2 | +4.4 | **28.8 ms** |

**殘差不隨 buffer 改變** —— 若它正比於 FMOD 緩衝，512 下該掉到 15.6ms，實測沒掉。所以它是固定的驅動延遲，
寫死即可。取 33（＝聽覺中位數）而不是 31：那 ~2ms 是輸入延遲（Update 輪詢＋鍵盤），一併吸收，
**跟著音樂打的人 delta 才會真的落在 0**。

推論：兩種 buffer 下需要的總補償都是同一個數 → `m_DSPBufferSize` 從此純粹是「會不會爆音」的取捨，
**換它不必重新校時**。這正是「時鐘讀播放游標」換來的東西。

osu!lazer 的處境與解法完全相同（`FramedBeatmapClock.WINDOWS_BASE_AUDIO_OFFSET = 15`，實驗性 WASAPI 再 −25，
外加使用者 `AudioOffset`）—— 差別只在我們這個數字是實測的，不是猜的。

於是 **`globalOffsetMs` 預設 0**，回歸本意：使用者的個人偏好（想打早/晚一點），以及別台機器驅動延遲不同時的微調。

> **校時一定要用耳朵。**「看著 note 打」是拿時鐘畫出來的東西去對時鐘 —— 自我參照，
> 它只量得到輸入延遲（本機 +1.6 ~ +4.4ms），**永遠量不到音訊延遲**。兩個測法的差值才是音訊延遲。

單首 offset 的實作是把它加進**音樂的 count-in**（`ScreenGameplay.MusicCountInSec` = type-10 無聲數拍 ＋ 單首 offset），
而 dsp ↔ 譜面時間的換算一律走這個值。錨點與 count-in 一起搬時「譜面時間 → dsp」的映射是不變的
（`anchor' = anchor + Δ/rate`），所以**打拍音仍然對在音符上**，只有音樂本身被挪走 —— 這正是 StepMania 調 offset 時的體感。

## 打拍測試（校時）— F2

不讀 .gn、不放音樂：只有**固定 BPM 的等距音符**（全部落在 R 軌）＋每顆音符一聲節拍音（＝既有的 assist tick）。
跟著節拍音打 `D`（或 `→`），畫面下方會出現 **osu 式的打擊誤差橫條**：

* 橫條左右兩端 = ±最大判定窗（Miss 邊界），中央 = 剛好準時；**左邊 = 太早、右邊 = 太晚**。
* 每一擊畫一根 tick（依判定上色），5 秒內淡出；下面的箭頭是誤差的**指數移動平均**（同 osu 的 `floatingAverage`）。
* 右側面板即時給：打擊數、平均、中位數、UR（＝10 × 標準差，越小越穩）、以及 osu 式的**誤差直方圖**。

**兩個可調的東西**（都能直接存進 `config.ini`）：

| 設定 | 意思 |
|---|---|
| `globalOffsetMs` | **判定 offset（毫秒）**。加在譜面時鐘上（`GameplayClock.OffsetMs` —— 這個鉤子本來就在，只是以前沒人設）。正 = 判定時間往後 → **整體打太早的人要調正的**。補的是「聲音→耳朵→手→鍵盤→引擎」整條延遲，每台機器不同。 |
| `judgeOffsetY` | **判定線位置（設計 px）**。完美時機的音符會落在「受擊線 + 這個位移」的地方（畫面上那條藍線），受擊線圖本身不動。純視覺，不影響判定時間。 |

面板會直接算**建議 offset**：作法照 osu（`BeatmapOffsetControl.computeSuggestedOffset`）——
`建議值 = 目前值 − 中位數`，而且打得越亂（UR ≥ 90）建議值越保守（乘 `exp(-0.0116 × (UR−90))`），
免得幾次亂打把 offset 帶歪。**用中位數不用平均**：一次離譜的晚打不該影響校正結果。

誤差的正負號與 osu 一致（`delta = 打擊時間 − 音符時間`，負 = 早、正 = 晚），跟判定引擎 `JudgeHit` 同一套。

## 測試

* `HitErrorStatsTests` / `BeatTestChartTests`（EditMode，純邏輯：建議 offset 的方向與衰減、直方圖分箱、合成譜間距）
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
