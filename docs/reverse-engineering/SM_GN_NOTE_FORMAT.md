# SM（StepMania）與 GN（StepFile）音符格式說明

本文件依 [`sm_gn_chart_tool.py`](sm_gn_chart_tool.py) 的實作整理，說明此工具如何解讀 **StepMania `.sm` 的 `#NOTES` 區** 與 **GN 內的 StepFile 二進位譜面**。若與遊戲引擎其他版本有差異，以實際檔案與工具行為為準。

---

## 一、GN / StepFile 二進位格式（本工具支援）

### 1.1 檔案外層：可選的 ddrm 包裝

- 若檔案開頭 4 位元組（小端序）為魔數 `0x6D726464`（ASCII `ddrm`），則前 **0x54（84）** 位元組為 **ddrm 表頭**，其後才是 StepFile 本文。
- 若無此魔數，則整檔視為 **純 StepFile 本文**（與解密後的 `.gn` 本文相同）。
- 工具輸出時可選擇保留原 ddrm 表頭，並更新其中的檔案總長度欄位。

### 1.2 StepFile 表頭（固定 300 位元組）

位元組序皆為 **little-endian**。下表以 **實際解密 SDOM 系 `.gn` 表頭**（例如 `de_sdom1219K.gn` 略過 ddrm 後）為準；自 offset **64** 起與舊版 Arrowgene／本工具 `StepFileBin` 讀寫的「字串區、duration、address」排列一致。

| 偏移 | 型別 / 長度 | 說明 |
|------|-------------|------|
| 0 | `int32` | `file_id` |
| 4 | 4 bytes ASCII，`\0` 結尾 | `file_type`（固定填滿 4 bytes，常為 `gn`） |
| 8 | 8 bytes | 前段保留／版本欄（實檔為二進位內容；**非**「單純 16×int16 再接到 level」） |
| 16 | `float` | **BPM**（音樂速度；與 offset 8–15 的位元組重疊解讀時，勿把 16–19 當成兩個 `int16`） |
| 20 | 3×`int16` | `level_easy`, `level_normal`, `level_hard`（譜面級數） |
| 26 | `int16` | 常為 `0`（padding／預留；見 §1.2.1） |
| 28 | 6×`int16` | 實檔可有非零值；**不是**舊版 offset 46 的 `post_level_h`（見 §1.2.1） |
| 40 | 3×`int32` | `note_count_easy`, `note_count_normal`, `note_count_hard`（三難度音符計） |
| 52 | 3×`int32` | 保留／引擎用欄（實檔可為他義；**不是**上列之主 `note_count`） |
| 64 | 3×`int32` | `measurements_easy`, `measurements_normal`, `measurements_hard` |
| 76 | 32 bytes | `raw_unknown_string0`（見 §1.2.1；與 Arrowgene 類版面對齊之「首段 32-byte 槽」） |
| 108 | 32 bytes | `raw_title`（UTF-8 字串，以 `\0` 截斷） |
| 140 | 32 bytes | `raw_unknown_string1`（見 §1.2.1） |
| 172 | 32 bytes | `raw_writer` |
| 204 | 32 bytes | `raw_producer` |
| 236 | 32 bytes | `raw_file_name` |
| 268 | `int32` | `unknown19` |
| 272 | 3×`int32` | `duration_easy`, `duration_normal`, `duration_hard` |
| 284 | 4×`uint32` | `address_easy`, `address_normal`, `address_hard`, `address_end` |

#### GN 表頭與 Songlist 欄位對照

以 `sdom2953K.gn` (file_id=12953) 為例：

| GN 表頭欄位 | GN 數值 | Songlist 對應欄位 | 說明 |
|-------------|---------|-------------------|------|
| `note_count` [40-51] | 664, 935, 1039 | `note_cnt` ✓ | LDUR 音符數（不含 type 9, 10） |
| `measurements` [64-75] | 347, 349, 349 | `measure` ✓ | StepFrame 總數量 |
| `duration` [272-283] | 159, 159, 159 | **無對應欄位** | 歌曲時長（秒）= 2分39秒 |
| — | 79, 79, 79 | `max_measure` | 最大小節編號 = max(measurement) |

**注意**：
- `duration` = 歌曲播放時長（秒），Songlist 中無此欄位
- `max_measure` = 譜面最大小節編號，由 `max(measurement)` 計算得出，GN 表頭中無此欄位
- `measurements` = StepFrame 總數量，與 `max_measure` 不同（同一小節可有多個 frame）

### 1.2.1 與反編譯 `FUN_0048e310`（`CNewNote::LoadNote`，[`sdo_stand_alone.exe.c`](../test/sdo_stand_alone.exe.c) 約 104213 行起）對照

表頭拷貝到物件內 **`this + 0x408`** 後，本函式可確認的讀點包括：`__stricmp((char*)(this+0x40c),"gn")`（檔案 offset **4**）、`*(float*)(this+0x410)`（檔案 **offset 8** 起之 float）、`*(float*)(this+0x418)`（檔案 **offset 16** 之 BPM，亦見 `FUN_0048db40`）、`*(uint*)(this+0x43c+…)`（檔案 **offset 52** 之索引用 n）、`*(uint*)(this+0x448+…)`（StepFrame 筆數）等。

以下欄位在 **`FUN_0048e310` 已讀取的程式範圍內**，**未見**「逐欄、具名」的存取（僅包含在 **`FUN_0040f450(this+0x408, 300)`** 一次讀入的 300 bytes 裡）：

| 表頭偏移 | 物件（`+0x408`） | 比對結論 |
|----------|------------------|----------|
| **26** | `+0x41a` | 未在此函式內目視到單獨讀寫；多為 **0**，推測 **padding／版本預留**。 |
| **28–39**（6×`int16`） | `+0x41c`…`+0x427` | 同左；實檔可有非零值，**語意需搜專案內其他對 `this+0x41c` 起算之讀點**或對照寫檔工具。 |
| **76–107**（`raw_unknown_string0`） | `+0x454`…`+0x473` | 未在此函式內見針對 **`this+0x454`** 的字串 API；版面與 Arrowgene 系「首段 32-byte 字串槽」常見位置對齊，**推測為曲目／選單用預留字串或副資訊**，實際使用可能在 **其他函式**（全檔搜尋 `+ 0x454` 或字串相關呼叫之指標來源）。 |
| **140–171**（`raw_unknown_string1`） | `+0x494`…`+0x4b3` | 同上；**未在 `FUN_0048e310` 內見**對 `this+0x494` 區塊的具名字串處理。 |

因此：**僅靠 `FUN_0048e310` 無法「定名」這幾個欄位在遊戲內的用途**；只能結合（1）二進位版面與 Arrowgene 慣例、（2）全執行檔對 **`this+0x408` 加固定偏移** 的交叉引用，再逐步收斂。

**`StepFileBin`（`sm_gn_chart_tool.py`）：** 會以 `stepfile_header_sdom_summary` 自動判斷表頭前段；若為 SDOM 則依上表讀寫 **8–63**（含 `bpm`、`level_*`、`note_count_*`、`extra52_*` 等），否則採 **legacy**（**8–39** 為 **16×`int16`** `unknowns_a`，**40** `level_*`、**46** `post_level_h`、**52** `note_count_*`）。`encode()` 會依載入時的 `_header_layout` 寫回對應排版。僅表頭除錯可用 **`stepfile_header_print.py`**。

**約束（本工具解析時會檢查）：**

- 表頭總長 **必須為 300**。
- `address_easy` **必須等於 300**（譜面資料緊接在表頭後）。

三個難度的譜面資料區間為：

- Easy：`[address_easy, address_normal)`
- Normal：`[address_normal, address_hard)`
- Hard：`[address_hard, address_end)`

### 1.3 StepFrame（單一「譜面帧」）

每個難度區段由多個 **StepFrame** 串接而成，格式如下：

| 欄位 | 型別 | 說明 |
|------|------|------|
| `measurement` | `int32` | 時間軸上的「小節／拍點」編號（整數索引，非 SM 的拍號分數） |
| `step_frame_type` | `int16` | 軌道／帧種類（見下節） |
| `interval` | `uint16` | 該帧內的 **格數**（列數）；每格 4 bytes |
| 重複 `interval` 次 | 每格 4 bytes | 一個 **slot**（見下節） |

**Slot（每格 4 bytes）：**

| 欄位 | 型別 | 說明 |
|------|------|------|
| `u0` | `int16` | 非 0 表示此格有音符；本工具寫入箭頭時固定為 **1** |
| `u1` | `uint8` | 本工具寫入箭頭時固定為 **0** |
| `step_note_type` | `uint8` | 音符類型（見下節） |

全 0（`00 00 00 00`）表示該格 **空**。

### 1.4 `step_frame_type`（與 SM 四鍵的對應）

本工具只從 **dance-single** 轉入箭頭，對應 Arrowgene 的 `StepFrameType` 如下（與程式常數 `COL_TO_FRAME_TYPE` 一致）：

| SM 欄位（由左至右） | 意義 | `step_frame_type` |
|---------------------|------|-------------------|
| 第 1 欄 | Left | **2** |
| 第 2 欄 | Down | **3** |
| 第 3 欄 | Up | **4** |
| 第 4 欄 | Right | **5** |

#### 其他 `step_frame_type`（非四方向軌）

| `step_frame_type` | 意義 | 說明 |
|-------------------|------|------|
| **1** | BPM／特殊標記 | 用於 BPM 變化等控制資訊 |
| **9** | 小節線 | 小節分隔標記 |
| **10** | 音樂起止 | 音樂開始與結束標記 |

**注意**：GN 表頭的 `note_count` 欄位**只計算 type 2, 3, 4, 5（LDUR）的音符**，不包含 type 1, 9, 10 等特殊類型。

合併譜面時工具會 **保留原 GN 該小節的 frame 骨架與順序**，只覆寫與 LDUR 四種 type 對應的箭頭資料。

#### Type 1（BPM／特殊標記）詳細格式

Type 1 用於標記 BPM 變化，slot 的 4 bytes (`u0` + `u1` + `nt`) 合併解讀為 **float32 (little-endian)**：

```
slot 結構: [u0: int16][u1: uint8][nt: uint8]
解讀方式: struct.unpack('<f', struct.pack('<hBB', u0, u1, nt))
結果: BPM 值（如 120.0、140.5）
```

`stepfile_dump2.py` 中的處理：

```python
raw_b = struct.pack("<hBB", e["u0"], e["u1"], nt)
bpm_val = struct.unpack("<f", raw_b)[0]
```

#### Type 9（小節線）詳細格式

Type 9 標記小節分隔線，通常放在**奇數** measurement 位置（1, 3, 5, 7...）。

| 欄位 | 數值 | 說明 |
|------|------|------|
| `measurement` | 1, 3, 5, 7... | 小節線所在的小節位置 |
| `interval` | 通常為 1 | slot 數量 |
| `u0` | 遞增整數 | 小節編號（從 2 開始遞增） |
| `u1` | 0 | 固定為 0 |
| `nt` | 0 | 固定為 0 |

**範例**（sdom2953K.gn Easy）：

```
measurement=1  -> u0=2  (第 2 個小節線)
measurement=3  -> u0=3  (第 3 個小節線)
measurement=5  -> u0=4  (第 4 個小節線)
...
measurement=79 -> u0=41 (第 41 個小節線)
```

**數量關係**：type 9 數量 ≈ max_measure / 2（因為只放奇數位置）

#### Type 10（音樂起止）詳細格式

Type 10 標記音樂的開始與結束，通常只有 **1 個 frame**，放在 measurement=1 的位置。

| 欄位 | 數值 | 說明 |
|------|------|------|
| `measurement` | 1 | 固定在開頭 |
| `interval` | 1 | slot 數量 |
| `u0` | 1000 | 固定標記值 |
| `u1` | 0 | 固定為 0 |
| `nt` | 0 | 固定為 0 |

**範例**：

```
Frame: measurement=1, interval=1
  slot[0]: u0=1000, u1=0, nt=0
```

### 1.5 `step_note_type`（與 slot 的 `u0`/`u1`）

本工具依 SM 字元寫入的組合為：

| `step_note_type` | 本工具寫入的 (`u0`, `u1`) | 含義（對齊程式註解） |
|------------------|---------------------------|----------------------|
| **0** | `(1, 0)` | 一般箭頭（ARROW） |
| **2** | `(1, 0)` | Hold 起點（HOLD_START） |
| **3** | `(1, 0)` | Hold 結尾（HOLD_END） |

空格為 `u0 == 0` 的 slot。

---

## 二、StepMania `.sm` 音符格式（本工具支援子集）

### 2.1 支援範圍

- **譜面類型**：僅 **`dance-single`**（四鍵）。
- 工具由 **`#NOTES:`** 起解析，且該區塊第二個非空行必須為 `dance-single`（大小寫不敏感，可帶結尾 `:`）。

### 2.2 `#NOTES:` 區塊行序（與 `parse_sm_chart` 一致）

1. 找到 `#NOTES:`。
2. 第一個非空行：`dance-single`（或 `dance-single:`）。
3. 下一個非空行：描述／作者（常為 `:`），**會讀取但轉譜不依賴內容**。
4. 下一個非空行：**難度名稱**（如 `Challenge`、`Hard`），須與 UI 指定的 SM 難度相符。
5. 下一個非空行：**meter**（整數，通常為譜面「級數」；解析失敗時預設 1）。
6. 若下一行像 `0.000=...` 且含逗號的 **groove radar** 行則跳過。
7. 之後為譜面資料行，直到單獨一行的 **`;`** 結束。

註解：行內 **`//` 之後** 視為註解並會先去掉再解析。

### 2.3 譜面資料列（measure 與 row）

- **小節分隔**：以 **單獨一行的逗號** `,` 開始新小節（或空行＋結構上的逗號行，邏輯上等價於 SM 慣例）。
- **每一資料列**：去掉空白後必須剛好 **4 個字元**，對應 **L, D, U, R** 四欄。
- 字元會轉成小寫再對照（故 `M` 與 `m` 同等）。

### 2.4 SM 字元 → GN slot 對照（本工具）

| SM 字元 | 行為 |
|---------|------|
| **`0`** | 空；該格不寫入音符 |
| **`1`** | 箭頭：`step_note_type = 0`，`u0=1`, `u1=0` |
| **`2`** | Hold 起點：`step_note_type = 2` |

| **`3`** | Hold 結尾：`step_note_type = 3` |
| **`4`**、`**M**`、`**m**` | **略過**（不寫入；地雷等不支援） |

### 2.5 SM 小節與 GN `measurement` 的對應（概念）

- SM 第 **1** 小節、第 **2** 小節… 在轉成 StepFrame 時，預設：
  - `measurement = SM 小節序號（從 1 起） + 小節編號偏移（measure_offset）`
- **`#OFFSET`（秒）** 與 **`#BPMS`** **不會**自動改變上述「小節偏移」；若要對齊音樂，請使用工具 UI 的 **整譜時間平移**（對所有 SM 轉出的 `measurement` 加整數 N），或自行調整偏移。
- 工具提供以 `#OFFSET` 與第一段 BPM **粗估**平移拍數的按鈕（公式：`round(OFFSET × BPM / 60)`），僅供起點，實際仍需以耳調為準。

### 2.7 GN → SM 轉換的 OFFSET 計算

`sdo_gn_master_tool.py` 的 `stepfile_to_sm_text` 函數會將 GN 譜面轉換為 SM 格式，其中 `#OFFSET` 的計算邏輯如下：

#### 步驟一：計算 shift（偏移拍數）

掃描譜面中所有音符和 BPM 事件，找出**最小的全域拍數位置**：

```python
def infer_shift_from_frames(frames: List[StepFrame]) -> float:
    gbs: List[float] = []
    for fr in frames:
        if fr.step_frame_type not in (1, 2, 3, 4, 5): continue
        iv = max(1, fr.interval)
        for i, sl in enumerate(fr.slots):
            if fr.step_frame_type == 1:
                # BPM 事件（type 1）
                if _slot_bpm_if_present(sl) is not None:
                    gbs.append(fr.measurement * 4.0 + 4.0 * i / iv)
            else:
                # 音符事件（type 2-5）
                if sl and sl[0] != 0:
                    gbs.append(fr.measurement * 4.0 + 4.0 * i / iv)
    return min(gbs) if gbs else 0.0
```

- **全域拍數公式**：`global_beat = measurement × 4.0 + 4.0 × slot_index / interval`
- **shift** = 所有事件中最小的 global_beat 值（即第一個音符/BPM 事件的位置）

#### 步驟二：轉換為 OFFSET 秒數

```python
offset_sec = -shift × 60.0 / fbpm
```

| 變數 | 說明 |
|------|------|
| `shift` | 第一個音符的拍數位置 |
| `fbpm` | 第一個 BPM 值（每分鐘拍數） |
| `offset_sec` | SM 的 #OFFSET 值（秒） |

#### 公式原理

- SM 的 `#OFFSET` 定義：**音樂開始時，beat 0 距離現在有多久**
- 負值表示 beat 0 在音樂開始之前（譜面提前）
- 將拍數轉為秒數：`拍數 / BPM × 60 = 秒數`
- 取負號是因為 GN 的 shift 表示「第一個音符在第幾拍」，需要反推音樂應提前多久

#### 實際範例

假設 GN 譜面：
- 第一個音符在 `measurement=2, slot=0, interval=4`
- BPM = 120

計算：
```
shift = 2 × 4.0 + 4.0 × 0 / 4 = 8.0 拍
offset_sec = -8.0 × 60.0 / 120 = -4.0 秒
```

結果：SM 檔案的 `#OFFSET:-4.000000;` 表示音樂開始前 4 秒是 beat 0。

#### GN ↔ SM OFFSET 轉換對照表

| 方向 | 公式 | 說明 |
|------|------|------|
| GN → SM | `offset_sec = -shift × 60 / bpm` | shift = 第一個音符的拍數位置 |
| SM → GN | `shift = -offset × bpm / 60` | 將 OFFSET 轉回拍數偏移 |

這兩個公式互為**反向操作**，理論上可以無損轉換。

---

### 2.8 檔案尺寸優化（interval 壓縮機制）

GN 格式的檔案大小**高度依賴 interval 設定**。不當的 OFFSET 設定可能導致檔案膨脹 **5-32 倍**。

#### StepFrame 大小與 interval 的關係

每個 StepFrame 的大小 = `8 + interval × 4` bytes

| interval | 對應節拍 | 每幀大小 | 說明 |
|----------|----------|---------|------|
| 4 | 4 分音符 | **24 bytes** | 最小（最佳壓縮） |
| 8 | 8 分音符 | 40 bytes | |
| 12 | 12 分音符 | 56 bytes | 三連音 |
| 16 | 16 分音符 | 72 bytes | |
| 24 | 24 分音符 | 104 bytes | |
| 32 | 32 分音符 | 136 bytes | |
| 48 | 48 分音符 | 200 bytes | |
| 192 | 192 分音符 | **776 bytes** | 最大（無法壓縮） |

**膨脹比例**：776 ÷ 24 = **32.3 倍**

#### 壓縮原理（optimize_frame 函數）

```python
def optimize_frame(fr: StepFrame) -> StepFrame:
    """將 interval=192 的 frame 壓縮到最小需要的 interval"""
    used = [i for i in range(192) if fr.slots[i] is not None]
    if not used:
        # 空幀：使用 interval=4
        return StepFrame(fr.measurement, fr.step_frame_type, 4)
    
    # 嘗試各種可能的 interval（從小到大）
    for snap in [4, 8, 12, 16, 24, 32, 48, 64, 96, 192]:
        step = 192 // snap  # 每個 slot 佔多少 192-grid 格
        if all(p % step == 0 for p in used):
            # 所有音符都落在此 snap 可整除的位置
            new_fr = StepFrame(fr.measurement, fr.step_frame_type, snap)
            for p in used:
                new_fr.slots[p // step] = fr.slots[p]
            return new_fr
    return fr  # 無法壓縮，維持 interval=192
```

**壓縮條件**：所有音符的 slot index 必須能被 `192 / interval` 整除。

#### OFFSET 如何影響壓縮

SM 轉 GN 時，音符位置的計算：

```python
gb = mi * 4.0 + (ri * 4.0 / rc) + shift  # 全域拍數
si = int(round((gb % 4) / 4.0 * 192))    # slot index (0-191)
```

| 情況 | shift 小數部分 | slot index 範例 | 可壓縮到 |
|------|---------------|-----------------|----------|
| 良好 | 0.0 | 0, 48, 96, 144 | interval=4 |
| 良好 | 0.5 | 24, 72, 120, 168 | interval=8 |
| **糟糕** | 0.271 | 13, 61, 109, 157 | interval=192（無法壓縮） |

**問題**：13 無法被 48、32、24、16、12、8、4 整除，只能用 interval=192。

#### 自動格點對齊（避免膨脹）

工具會自動將 shift 的小數部分對齊到可壓縮的格點：

```python
# 將 shift 的小數部分對齊到 192-grid 可整除的格點
_SNAP_STEPS = sorted({192 // s for s in [4, 8, 12, 16, 24, 32, 48]}, reverse=True)
raw_frac_rows = (shift % 4.0) / 4.0 * 192  # 小數部分轉為 192-grid 位置

# 找最近的可整除格點
for step in _SNAP_STEPS:
    snapped = round(raw_frac_rows / step) * step
    if abs(snapped - raw_frac_rows) < best_d:
        best_d = abs(snapped - raw_frac_rows)
        best_step = step

# 修正 shift
shift = shift + shift_correction
```

**可對齊的格點**（192-grid 中的位置）：
- 48 分音符對齊：0, 4, 8, 12, 16, 20, 24, 28, ... (間隔 4)
- 32 分音符對齊：0, 6, 12, 18, 24, 30, ... (間隔 6)
- 24 分音符對齊：0, 8, 16, 24, 32, 40, ... (間隔 8)
- 16 分音符對齊：0, 12, 24, 36, 48, 60, ... (間隔 12)
- 12 分音符對齊：0, 16, 32, 48, 64, 80, ... (間隔 16)
- 8 分音符對齊：0, 24, 48, 72, 96, 120, 144, 168 (間隔 24)
- 4 分音符對齊：0, 48, 96, 144 (間隔 48)

#### 格點對齊造成的音樂偏移量

格點對齊會**微調 shift 值**，使音符落在可壓縮的位置。這會導致整體譜面產生微小的時間偏移。

**最大偏移計算**：

最密的可用格點是 48 分音符（間隔 4 格），最大偏移為 2 格（到最近格點的一半距離）：

```
最大偏移拍數 = 2 / 192 × 4 = 0.0417 拍（1/24 拍）
```

**不同 BPM 下的最大偏移時間**：

| BPM | 最大偏移（毫秒） | 說明 |
|-----|-----------------|------|
| 60 | **41.7 ms** | 慢歌 |
| 90 | **27.8 ms** | |
| 120 | **20.8 ms** | 常見 BPM |
| 150 | **16.7 ms** | |
| 180 | **13.9 ms** | 快歌 |
| 200 | **12.5 ms** | |

**偏移公式**：

```
最大偏移（秒）= 2 / 192 × 4 × 60 / BPM = 2.5 / BPM
最大偏移（毫秒）= 2500 / BPM
```

**對遊戲體驗的影響**：

- 人耳對音樂同步的感知閾值約為 **20-50 毫秒**
- 在常見 BPM（100-180）範圍內，最大偏移為 **14-25 毫秒**
- **結論**：格點對齊造成的偏移量在人耳可接受範圍內，幾乎不影響遊戲體驗

**權衡**：

| 選擇 | 檔案大小 | 音樂偏移 |
|------|---------|---------|
| 不對齊（interval=192） | **大**（可能膨脹 32 倍） | 0 ms |
| 對齊到 48 分音符 | **小**（最佳壓縮） | 最大 ~21 ms (120 BPM) |

對於大多數情況，**檔案大小優化的收益遠大於微小偏移的代價**。

#### 檔案大小估算

假設一首 100 小節的歌曲，每小節平均 4 個音符 frame：

| interval | 每幀大小 | 總大小（400 frames） |
|----------|---------|---------------------|
| 4 | 24 bytes | **9.6 KB** |
| 8 | 40 bytes | 16 KB |
| 16 | 72 bytes | 28.8 KB |
| 192 | 776 bytes | **310 KB** |

**結論**：正確對齊 OFFSET 可以讓檔案縮小 **5-32 倍**。

---

## 三、轉換時的幾個行為重點（讀格式時一併理解）

1. **note 計數**：`note_count_*` 在對齊時間軸後會設為 **`max(measurement) + 1`**（與工具 log 說明一致）。
2. **三難度時間軸**：寫入單一難度後，會拉齊三難度的最大 `measurement`，避免引擎以同一時間軸讀譜時長度不一造成問題。
3. **保留開頭帧**：可選保留原 GN 中 `measurement` 小於新譜第一個 measurement 的帧（常含 0/1 等標記）；可選僅保留骨架並 **清空音符**，避免舊箭頭殘留。

---

## 四、參考

- 實作與註解：[`sm_gn_chart_tool.py`](sm_gn_chart_tool.py)
- 概念參考：Arrowgene `StepFile.java` / `StepFrame.java`；StepMania MSD 式 `.sm` 規格（本工具僅實作其中與 `dance-single` 相關且上表列出的子集）。
