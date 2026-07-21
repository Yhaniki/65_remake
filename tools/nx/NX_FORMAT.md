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

## 2. 加密（**已完全破解，可離線解，用 `crack_nx.py`**）

`.nx` 是 **兩層**：patcher 外層固定變換 + 遊戲原本的 0x3D09 LCG。

### 2.1 遊戲怎麼讀到 `.nx`（patcher 的三個 hook）

遊戲程式碼**只會**組出 `.gn` 檔名（唯一的格式字串 `"%sgn"` @ `0xf3b0e0`），是 patcher 在中間動手腳：

1. **路徑改寫**（`CreateFileA` hook `0x1249373`，只在 `dwCreationDisposition==3` 即 OPEN_EXISTING 時）
   路徑開頭是 `music\` → 改成 `patch music\`（檔名原樣接上）。另有 `CreateFileW` 版。
2. **副檔名改寫**（`.gxl` 段 `0x124b076`）
   改完路徑後跳到這裡：若結尾是 `.gn`（不分大小寫）→ 直接改成 `.nx`，然後才真的 CreateFile。
   ```
   music\sdom2818K.gn  →  patch music\sdom2818K.gn  →  patch music\sdom2818K.nx
   ```
3. **CRC 檢查繞過**（`.g20` 段 `0x124d036`，由容器讀取器 `0xd6ec16` 跳入）
   checksum 不符時，只要旗標 `[0x124c000]==1` 就照樣放行（patch 檔內容變了，CRC 當然對不上）。

### 2.2 外層：patcher 的固定變換（**無金鑰**）

解密函式 `0xb1c950` 被 inline hook（`jmp 0xb1c950 → 0x124e000`，`.g22` 段）。
`.g22` 在原本的 LCG **之前**先對每個 byte 做：

```c
b ^= 0xA7;  b -= 0x29;  b = (b * 0xF9) & 0xFF;  b = ror8(b, 3);
```

**沒有金鑰、與位置無關**（純混淆）。只在 `length > 0x100` 且旗標 `[0x124c000]==1` 時套用。
> 那段從 `[esi-4] … [esi-0x10]` 和 `0xA5C3F17B` 算出來的 `edx` **完全沒被用到** —— 是誘餌。

### 2.3 內層：0x3D09 LCG（seed 可離線還原）

還原外層後就是舊的那套（`0x124e078` 起，等同離線版 `FUN_0048bf50`）：
```
state *= 0x3D09;  plain = cipher − ((state >> 16) & 0xFF)
```
執行期 seed 來自 `[ctx+0x90]` 的 ddrm 標頭（`[0xc]=seed1` 解出 block → `[0x24]=seed2`），
呼叫鏈 `0x7f52a9/0x9b1726 → 0x7f70c0 → 0xd6f0b0 → 0xd6e9b0`。

**但不需要它** —— blob 解出來的**前 300 bytes 就是「重複表頭」**，內容等於檔案 `0x1C8` 那份
**明文**表頭。拿它當已知明文即可反推 seed：keystream 只取 state 的 bit16–23 → 只依賴低 24 位
→ 向量化掃 2²⁴ 個等價 state，唯一解。**seed 會在檔案間重複用**（實測 199 個 `.nx` 只有二十幾種），
先試已知 pool、沒中才暴力。

> 驗證重複表頭時**不要**逐位元組比對容器表頭：少數檔 metadata 會差幾個 byte，而且容器表頭的
> `address_end(+296)` 本來就是垃圾值。改用結構條件：`[4:8]=='gn\0\0'`、fileId 一致、
> `address_easy==300`、四個 address 單調遞增且 ≤ blob 長度。
> （也**不能**要求 `address_end == blob 長度` —— 有內嵌 dps/png/ogg 的檔，note 資料只佔 blob 前段。）

**驗證**：`sdom2818K.nx` 全檔離線解出的 1,384,076 bytes，與從遊戲記憶體 hook `0xb1c950` dump 到的明文
**byte-for-byte 完全一致**。

**對照**：`music\*.gn`（已安裝的）**沒有**外層變換，直接就是 0x3D09 + 重複表頭 → 同樣可離線解。

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
