# `.nx` 譜面容器格式（NXPatch 私服/線上客戶端）

`H:\sdo\Super Dance Online\patch music\*.nx` 是 **NXPatch.exe** 用的譜面容器。
**同一個格式也用在 `music\sdomNNNNK.gn`** —— 副檔名叫 `.gn`，但內容是這個容器（不是舊的 ddrm/sdom `.gn`）。

逆向對象：`sdom2818K.nx`（"3" by Laur）、`sdom5046K.nx`、`sdom0040K.nx`，以及 `NXPatch.exe` / `DDROnline_D.exe`。

---

## 1. 容器版面（明文，逐欄驗證過）

| 偏移 | 長度 | 內容 |
|------|------|------|
| `0x000` | 6 × 32 bytes | 資源名表（NUL 補滿 ASCII）：`sdomNNNNK.gn`、`<fileId>.dps`、`<fileId>.png` ×3、`<fileId>.ogg` |
| `0x0C0` | u32 | 常數 `4` |
| `0x0C4` | u32 | 常數 `1` |
| `0x1C4` | u32 | 常數 `1` |
| `0x1C8` | 300 | **StepFile 表頭（明文）** —— 欄位版面與舊 k.gn 完全相同 |
| `0x2F4` | 到檔尾 | **加密 blob**（note 資料）。長度 = `filesize − 0x2F4` |

`fileId = 10000 + 曲號`（例：sdom2818 → 12818）。

### StepFile 表頭（`0x1C8` 起，300 bytes）

與 `docs/reverse-engineering/SDOM_STEPFILE_HEADER.md` 同一版面：

| +偏移 | 型別 | 欄位 |
|-------|------|------|
| 0 | u32 | `file_id` |
| 4 | 4B | `'gn\0\0'` |
| 8 | float | 版本（實測固定 2.9） |
| 16 | float | **BPM**（顯示用） |
| 20 | 3×i16 | `level` easy/normal/hard |
| 40 | 3×u32 | `note_count` easy/normal/hard（**含炸彈**） |
| 64 | 3×u32 | `measurements` |
| 108 / 172 / 204 / 236 | 32B | title / artist / producer / origName |
| 272 | 3×u32 | `duration`（秒） |
| 284 | 4×u32 | `address` easy / normal / hard / end（相對 StepFile 起點；easy 必為 300） |

> 注意：**容器明文表頭的 `address[3]`（+296）是垃圾值**，因為那 4 bytes 已落在加密區之前的邊界。解密後 blob 內的表頭才有正確的 `address_end`。

---

## 2. 加密（**離線解不開，這是 DRM**）

- **Cipher = 遊戲原本的 0x3D09 LCG**，和舊 `.gn` 完全同一套：
  ```
  state *= 0x3D09;  plain = cipher − ((state >> 16) & 0xFF)     // 逐 byte
  ```
  NXPatch.exe `FUN 0xb1c950(seed, start, end)`；離線版對應 `FUN_0048bf50`。
- **容器讀取器 `FUN 0xd6e9b0`**：開檔 → 取 filesize → `bodyLen = filesize − 0x2F4` → `SetFilePointer(0x2F4)` → `ReadFile(blob, bodyLen)` → `0xb1c950(seed2, blob, blob+bodyLen)`。
- **seed 來源（關鍵）**：`seed2 = [param+0x24]`，param 是一個 **0x54-byte ddrm 標頭**
  （`[0]='ddrm'`、`[4]=1`、`[8]=bodyLen+0x54`、`[0xc]=seed1`、`[0x20:0x54]`=用 seed1 解出的 block、`[0x24]=seed2`）。
  這個標頭 **不在 `.nx` 檔裡**，而是執行期從**全域 context `[ctx+0x90]`**（`ctx = FUN 0x4026a0()`）拿的。
  呼叫鏈：`0x7f52a9 / 0x9b1726` → `0x7f70c0`（存進 `obj+0x1bcc8`）→ `0xd6f0b0` → `0xd6e9b0`。

**對照**：舊的 classic ddrm `.gn`（如 `music\sdom0.gn`）走 `FUN 0xd6c360`，**從檔案前 0x54 bytes 讀 ddrm 標頭**（seed 在檔內）→ 可離線解。
新容器把 seed **搬出檔案** = DRM 強化。

**實測**：Frida 起 NXPatch，開機期間 `[ctx+0x90]` 全程是 **null**、`0xb1c950` **不觸發**；只有真正進歌載譜時才由連線填入。
→ **光有 `.nx` 檔無法離線解密**（連舊 SDOM 那種 148-seed pool 都沒有，是 per-song server-provided）。

已排除的猜測（都試過不成立）：0x3D09 全 2²⁴ 暴力（doubled / 非 doubled、起點 296/300）、zlib/bz2/lzma、週期 XOR、跨檔固定金鑰（兩檔密文 XOR = 純亂數）、容器頭內找 seed。

### 唯一實用取得法
真伺服器正常玩到該曲，用 `dump_chart.py` hook `0xb1c950` 把解密後 blob 存下來（見 README）。

---

## 3. 解密後的 note 格式

blob 解密後 = **`[300-byte StepFile 表頭][note 資料]`**，`address[]` 相對 blob 起點（easy 從 300 開始）。
`address[3]` 之後是其餘資源（dps/png/ogg），note 解析不要越過它。

### StepFrame
```
int32  measurement      // 小節
int16  step_frame_type  // 見下
uint16 interval         // 本幀格數
{ int16 u0; uint8 u1; uint8 note_type } × interval   // 每格 4 bytes，全 0 = 空
```
`beat = measurement*4 + 4*i/interval`（GnChart 慣例）；編輯器顯示的「小節內拍」= `1 + (i/interval)*4`。

### frame_type

| 值 | 意義 |
|----|------|
| 1 | BPM 變化（slot 4 bytes = float32） |
| **2 / 3 / 4 / 5** | 音軌 Left / Up / Down / Right（`lane = type − 2`） |
| 6 / 7 / 8 | 引擎支援到 7 軌（4 鍵不用） |
| 9 | 小節線（u0 = 遞增小節號） |
| 10 | 音樂起止（u0 & 0xfff == 1000 → 起、998 → 止） |
| 11 | 結束標記 |
| 12–19、27–30 | 事件通道（相機/特效等，未逐一命名） |
| **33** | **捲動速度**（見下） |

### note_type（音軌幀的 slot 第 4 byte）

| 值 | 意義 |
|----|------|
| 0 | 一般音符 |
| **1** | **炸彈**（要避開，不是拿來打的） |
| 2 / 3 | 長條頭 / 長條尾 |

> `note_count` 表頭欄位**包含炸彈**。實測 sdom2818 hard：1525 tap + 66 hold + **20 bomb** = 1611 = 表頭值。

### frame_type 33 — 捲動速度

slot 4 bytes 拆兩半：

| 位元 | 意義 |
|------|------|
| 低 16 位 (`u0`) | **速度值，1000 = 原速 ×1.0**（→ 倍率 = 值 / 1000） |
| 高 16 位 | **線性變速時長**，單位 **1/48 拍**（= 192/小節 tick）。`0` = 瞬間切換 |

語意：**把當下捲動速度設成該值**，維持到下一個事件。有時長就從前一個倍率**線性**過去。

sdom2818 實例（hard）：開頭 `2000`(×2)、小節6 `10000`(×10)/`1000`(×1)、小節8-9 一段 ramp、
小節34 `20000`(×20)；**線性**的只有這幾顆：小節7 b1.5(→×0.25, 5.5拍)、9 b2.5(0.5拍)、23 b3.0(1拍)、
38 b4.0(3拍)、39 b3.0(1拍)、44 b4.0(2拍)、45 b2.0(2拍)、52(1拍 / 0.25拍)，其餘皆瞬間。

> 驗證：使用者在官方編輯器讀到的位置（小節6 拍1.0/2.5/4.0、小節20 拍3.25、小節21 拍4.0）與本表解出的
> frame_type 33 事件位置**完全吻合**，且「小節7 線性變慢」正對上唯一帶時長的那顆。

---

## 4. 重製版實作對照

| 項目 | 位置 |
|------|------|
| 解析 type-33 / note_type 1 | `Sdo.Osu/GnChart.cs`（`Frame33SpeedBase = 1000`） |
| 捲動速度軌 | `Sdo.Osu/OsuBeatmap.cs`：`ScrollSpeeds` + `CurrentScrollSpeed()`（階梯 + 線性 ramp） |
| 套用捲動 / 炸彈顯示引爆 | `Game/ScreenGameplay.cs`：`ScrollPx()`、`TickBombs()`、`ExplodeBomb()` |
| 炸彈圖 | `DATA/NOTEIMAGE/NOTEIMAGE_*/ZD00..ZD03.PNG`（隨 note skin） |
| 爆炸圖/音 | StepMania：`Fallback Tap Explosion Dim HitMine.png` → `DATA/NOTEIMAGE/BOMB_EXPLODE.png`、`Player mine.ogg` → `DATA/SE/player_mine.wav` |

StepMania 的爆炸行為逐字照 noteskin metrics `[GhostArrowDim] HitMineCommand`：
`blend,add`（黑底靠加法混合去背）、`zoom,1`（不縮放）、`rotationz 0→90→180` 各 `linear,0.3`（等速 300°/s、共 0.6s）、
後半段才 `diffusealpha,0`。`[ReceptorArrow] HitMineCommand` 是**空的** → 踩地雷時受擊線**沒有**動畫。
