# SDO 音符背板 (NOTES_BOARD) 與血條光效 反編譯考據

> 來源：`assets/sdo_stand_alone.exe`(.c + capstone 反組譯) 與
> `assets/sdox_offline/Extracted/UI/GAMEPLAY/DDRGAMEPLAY.XML`、`Extracted/NOTEIMAGE/`。

## 1. 背板 noteskin / NOTES_BOARD 載入

JAM/DDR 遊玩物件初始化（`.c` 行 111244 起，`Datas\NoteImage.bin`）以 `FUN_0042cc20` 依**檔名**載入背板，存進物件欄位（位元組偏移 = `[index]*4`）：

| 檔名 | 欄位 index | 位元組偏移 | 用途 |
|------|-----------|-----------|------|
| `notes_board1.png` | `[0x421c]` | `+0x10870` | 主背板（**4 軌**軌道底圖，分隔線 69px 間距，見下） |
| `notes_board2.png` | `[0x421d]` | `+0x10874` | 背板變體 |
| `notes_board3.png` | `[0x421e]` | `+0x10878` | 背板變體 |
| `notes_board.png`  | `[0x421f]` | `+0x1087c` | 單軌條 (69×600) |
| `notes_board_miss.png` / `_miss1` | `[0x4225]` | `+0x10894` | Miss 軌條 (67×558) |
| `six_notes_board2` / `notes_board6` | — | — | 6key 模式 |
| `notes_board5` / `notes_board6_down` / `notes_board7` | — | — | 其他模式/skin |

圖檔尺寸（`Extracted/NOTEIMAGE/`）：

| 檔 | px | 說明 |
|----|----|------|
| `NOTES_BOARD1~3,6,7` | **315×600** | 全軌背板（**4 軌**；亮分隔線實測在 texture x=14/83/152/221/290，**間距 69px**＝與單軌條一致。內容區 x≈12..291，左右透明邊不對稱，置中時內容中心≈x152 偏左 ~6px。上緣 y0..11 全透明、y12 起為板面，頂角為倒角） |
| `NOTES_BOARD4,5` | 500×600 | 寬版 |
| `NOTES_BOARD.PNG` | 69×600 | 單軌 |
| `NOTES_BOARD_CLICK1` | 67×558 | 按下時亮起的軌條 |
| `NOTES_BOARD_MISS` | 67×558 | Miss 閃爍軌條 |
| `JAMLINE1/2.PNG` | 192×64 | JAM 判定線（橫） |

## 2. 背板捲動 = `0x10944`（不是血量！）

背板繪製函式 **`FUN_0048de50`**（`0x48de50`）用 `+0x10870+param_2*4` 當圖、用 `+0x10944` 當**垂直捲動位移**繪製（`600 - 0x10944` 當長度）。`0x10944` 由計時器 `FUN_0048ef60` 每 30 單位變動，**到 0 重設 600**——即背板材質**每 600px 循環捲動**的動畫（製造速度感），高度 600 = 背板圖高。

> ⚠️ 舊文件誤把 `0x10944`(0~600) 當血量；其實是背板捲動值。真正血量見
> [SDO_HP_FORMULA.md](SDO_HP_FORMULA.md)（`player[0]`, 範圍 -150~1000）。

## 3. 血條與「底端發亮光效」

`DDRGAMEPLAY.XML` 的 `WinMyHp`（單人血條）：

| 元素 | 圖 | 範圍 | 說明 |
|------|----|------|------|
| `myhp_progress` | back `bloodBG2.an` / fore **`MyHp.an`** | **-150 ~ 1000** | 主血條（驗證與 HP 公式一致） |
| `MyFullHp` | `FullHp.an` | — | 滿血外框/底圖 |
| `MyHpBack` | `MyHpBack.an` | — | 血條外框 |
| **`HpEft_Progress`** | back `HpEft_Back.an` / fore **`HpEft.an`** | **-150 ~ 159** | **血條前緣的發亮光效動畫** |

**光效位置**（`.c` 行 88904-88906）：
```c
HpEft.x = (HP + 150.0) * barPixelWidth * 0.00086956524 + base - ...
//        0.00086956524 = 1/1150 = 1/(1000 - (-150))
```
即把 HP∈[-150,1000]（跨度 1150）線性映射到血條像素寬，`HpEft.an` 這個動畫精靈就**貼在血量填充的前緣**跟著移動發光；血量越高光效越靠右。`HpEft.an` 本身是多幀動畫（`.an`）會自己循環播放發亮。

## 3.5 noteskin / 特效素材格式（`.DGN` / `.AN`）

全是**純文字**（CRLF 換行）：

- **`.DGN`**（note skin 定義）：列出該 skin 的 3 個 `.AN`，例如 `NOTE_IMAGE5.DGN`：
  ```
  NoteImage_5\judgeline.an
  NoteImage_5\noteimage.an
  NoteImage_5\keydown_judgeline.an
  ```
- **`.AN`**（動畫/影格清單）：**每行一個 PNG 檔名 = 一個影格**；同檔名連續多行 = 該圖顯示多幀（控制時長）。
  - `EFT_HITPERFECT.AN` = `perfect.png` ×N（命中特效播 N 幀）。判定字特效 = 單張 `PERFECT/COOL/BAD/MISS.PNG`。
  - `NOTEIMAGE.AN` / `NOTEIMAGE_MOVEDOWN.AN`：依序 **Left/Down/Up/Right** 各 4 幀（`*HoldHeadActive0~3.png`）+ 長條 body（`updown_long.png`…）。exe 以**索引**取用（方向×狀態）。
  - `JUDGELINE.AN` / `KEYDOWN_JUDGELINE.AN`：判定線上的接收器（receptor），依方向（Left/Down/Up/Right）取用。**幀數依 skin 不同**：
    - `_f2` 系列（如 `NOTEIMAGE_10/11`）：`JUDGELINE.AN` = `*_judgeline` + `*_judgeline_f2`（一般 / 按下，各 2 幀）。
    - **數字系列（如 `NOTEIMAGE_5`，遊玩實際採用）**：`JUDGELINE.AN`（idle）= `*_judgeline1.png`；`KEYDOWN_JUDGELINE.AN`（按下）= `*_judgeline` **2→3→4→5→6** 依序 5 幀。
  - **按下反饋語意（實測 + 反編譯）**：按鍵**按下瞬間**觸發一次性（one-shot）動畫——judgeline **2→3→4→5→6 依序播完，然後恢復回 idle 的 1**；**只看「按下」事件，不隨「按住」持續**（對應反編譯 `CtlNotesShow_TriggerLanePress_004c2690`＝「play judgeline press effect for a lane once」，且設一次性旗標防重觸發）。

**使用方式**：歌曲/設定選一個 skin 資料夾（`NOTEIMAGE_5/6/8/9/10/11`、`JAM_NOTEIMAGE`、`SIX_NOTEIMAGE_*`、`PET`、`SHOWTIME`…）→ 讀其 `.DGN` → 讀 3 個 `.AN` → 載入每行 PNG 當影格 → 依方向索引貼到 4(或6) 軌、依幀率播放。

## 3.6 軌條打擊光 `NOTES_BOARD_CLICK`（命中時整軌亮起）

note 被打擊時，**該軌**會整條亮起 `NOTES_BOARD_CLICK%d.PNG`（`%d` = 1~4 對應四軌；67×558，半透明青色 `(51,128,112)`、alpha 由上端~115 漸層到底端~27，最亮端在判定線）。

**載入**（`025_note_00498bd0.c` 行 2012-2029，`NoteImage.bin`）：四軌迴圈 `iVar8=1..4`，`sprintf("notes_board_click%d.png", iVar8)` → 存進欄位 `[0x4226+lane]`（KeyCfg byte`5c8==2` 時改用變體 `notes_board_click%d1.png`，即 `_CLICK11/21/31/41`）。

**繪製/動畫** `NoteBoard_DrawClickFlash_00498bd0`（`0x498bd0`）：在打擊中的軌道每幀重繪該軌條，頂點色用一張小色表，依**幀索引** `[+0x5d4]` 取色 → `NewNote_DrawNoteClipped`：

| 幀 | 色值 (ARGB) | alpha | 意義 |
|----|-------------|-------|------|
| 0 | `0xffffffff` | 255 | 全亮 |
| 1 | `0x82ffffff` | 130 | 半亮 |
| 2 | `0x00ffffff` | 0 | 全暗 |

計時器 `InputMgr_GetTime` 每 ~200 單位 `idx = (idx&3)+1; if(idx==3) idx=0`，即循環 **255→130→0**（白×alpha 調變整體不透明度）。打擊（tap）只在 note 通過判定窗時短暫顯示；長條（hold）按住時每幀重畫 → 整軌**持續脈動**。觸發點在 note 繪製迴圈（行 1351，gate `[+0x10994]!=0`）。

## 3.7 開場時序：NOTE BOARD 在開場鏡頭跑完才出現（state 3→4）

> 來源：`assets/sdox_offline/named/modules/gameplay/021_gameplay_0046b8a0.c` 行 6618-6885。
> 觀察（實機）：一開始進來**沒有 NOTE BOARD**，約等 5 秒讓鏡頭跑一下，才同時出現 NOTE BOARD 與「READY」字。

遊玩狀態機 `param_1+0x120`：

| state | 行 | 行為 |
|---|---|---|
| 2→3 | 6618-6749 | 載入 CDT 相機、`CameraSeq_Begin`（**開場 crane 導播**），進入開場運鏡 |
| **3** | 6771-6885 | **開場運鏡中**：`NoteBoard_Update_0049ff00` **不被呼叫**（它在 state≥4 的 `else` 分支，行 6887）→ 背板不更新/不顯示；notes 也未開始 |
| 3→4 | 6862-6884 | 開場結束 → `NewNote_StartPlayback_0048db40()`（notes 開捲，行 6871）+ 背板/READY 等 UI 物件一起啟用（`+4=1, +0x10=0, +0x21=0` 模式，行 6878-6883） |
| 4+ | 6886+ | 正常遊玩：`NoteBoard_Update` 每幀捲動背板 |

cinematic 開場（`+0xac1==1`）用另一套 **ScrollCam**（`ScrollCam_Init_00408440` / `ScrollCam_Update_00408910`，與 CDT 導播不同）：有 10000ms 子事件、15400ms(`0x3c28`) 上限（行 6829），但通常在 ScrollCam **到達目標點時提早結束**（行 6831）→ 實機目視 ≈5s。

**結論：NOTE BOARD + READY + notes 是在開場鏡頭結束時「一起」出現**，不是 t=0。crane 本身更長（palace_1.cdt shot0 = `cam0005\000.cv` = **12000ms**），會持續跑過這個揭示點（見 [SDO_CAMERA.md](SDO_CAMERA.md) §5）。

## 4. Unity 對應（已套用於 `Step1Game.cs`）

- **開場揭示（§3.7）已接上**：`Start()` 進場時若有 3D crane（`use3dCamera && _camReady`），先 `SetTrackVisible(false)` 把整個遊玩面板（背板 `_board` + 四軌 receptor + click strip + **血條** `_hpBg/_hpTex/_hpBackFrame/_hpGlow`）藏起、記下 `_introStartRt`；`OpeningSequence()` 開頭等 `openingIntroSec`（預設 **1s**；F4 滑桿可即時調，hold 中拖曳會即時生效）後才 `SetTrackVisible(true)` 揭示面板，緊接播 READY/GO 並啟動歌曲+notes。`UpdateHpBar()` 在 `_trackVisible==false` 時 early-out（不會把血條重新打開）。crane（導播 shot 0）從 t=0 持續跑過揭示點。2D / `avatarDebug` 無 crane 時 `_introStartRt<0`、面板照舊立即顯示。

- 背板（現行 `BuildBoard` / `ApplyBoardAlpha`）：直接從 `Extracted/NOTEIMAGE/NOTES_BOARD1.PNG` 載入**單張**精靈，疊在舞台 backdrop 上。
  - **必須 1:1 原生繪製**（`SdoLayout.PlaceTopLeft(boardX=0)`，不縮放）：因為圖內軌道線 = 設計座標 69px 格線，原生擺放時 texture x == design x，音符才會精準落在板上的軌道；任何縮放都會把格線拉歪（曾因 `boardWidth` 縮放置中導致右側偏 ~11px）。
  - **不透明度 = `boardAlpha` 對「原圖每像素 alpha」的倍率**（`SdoExtracted.AlphaScaledSprite`，值變動才重烤貼圖）：`1.0`＝完全等於原圖（原生 ~62%）、`~1.4`＝官方那種「深但內部斜紋/斜角細節仍可見」（**現行預設**）、`~2.6`＝全不透明。乘整條 alpha 曲線→相對明暗（細節）保留；全透明像素 `0×倍率=0`→倒角/切角恆透明，**不需任何不透明矩形墊底**。
    - ⚠️ 不可改用「載入時 ×k 後 clamp 到 255」再用 `color.a` 往下乘：那會把 alpha 曲線燒死，導致再也回不到原圖、也對不到官方深度（踩過的雷）。
  - 目前為**靜態**（未接 `0x10944` 垂直循環捲動）；要做捲動再另加。
- 血條光效：`UpdateHpBar` 中 `_hpGlow` 貼在填充前緣並做 sin 脈動（對應 `HpEft`）。
  - 若要像素還原，需把 `HpEft.an` / `MyHp.an` 等 `.an` 多幀動畫解出成 sprite sheet 再播放（目前用程序化脈動代替）。
- **打擊軌條光（`NOTES_BOARD_CLICK1~4.PNG`）已接上**（對應 §3.6 `NoteBoard_DrawClickFlash_00498bd0`）：
  - `LoadArt` 從 `Extracted/NOTEIMAGE/` 載入 `notes_board_click{1..4}.png`；`BuildBoard` 為每軌建一個 overlay `SpriteRenderer`（`PlaceTopLeft` 1:1 原生，置於 `LaneLeftX[c]+1`、頂端 y=12，sortingOrder −20＝在背板 −30 之上、receptor 0／note 5 之下）。
  - `ApplyEvent`（命中且非 Miss）→ `TriggerClickFlash(lane)`；`UpdateClickFlash` 每幀走 3 幀 alpha 循環 `ClickFlashAlpha={1, 130/255, 0}`（`clickFlashStepSec` 預設 0.07s/幀）：tap 播一輪即隱藏，hold（`_holding[lane]!=null`）持續脈動。白×alpha 直接調 `SpriteRenderer.color.a`（普通 alpha 混合，色帶在貼圖本身）。整體亮度再乘 `clickFlashBright`（預設 **0.4**，保留 255:130:0 比例；F4 滑桿可微調）。
- **判定線接收器按下反饋（`KEYDOWN_JUDGELINE.AN`）已接上**（對應 §3.5 + `CtlNotesShow_TriggerLanePress`）：
  - `LoadArt` 從 `NOTEIMAGE_5` 載入 idle `*_judgeline1.png`（`_recIdle`）與按下 5 幀 `*_judgeline2..6`（`_recDownFrames`）。
  - 觸發＝**按下事件**：手動（`HandleInput` 的 `down`）與 autoplay（`AutoPlay` 的 **note head** 命中，不含 hold tail）皆設 `_recDownStart[lane]=Time.time`。
  - `UpdateHud` 每幀依 `(now-start)/recKeydownStepSec` 取幀；播完 5 幀即 `_recDownStart=-1` 回 idle（frame 1）。**一次性、不隨按住持續**（呼應「只考慮按下」）。`recKeydownStepSec` 預設 **0.03s/幀**（≈150ms 全程；F4 滑桿可調）。
- Miss 軌條光（`NOTES_BOARD_MISS.PNG`）已複製到 Skin，尚未接到每軌觸發（待加，可比照 click 做法）。
