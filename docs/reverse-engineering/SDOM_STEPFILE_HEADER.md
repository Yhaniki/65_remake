# SDOM StepFile 表頭（300 bytes）

本文說明 **SDOM 系**解密後 `.gn` 內 **StepFile 本文**開頭固定 **300** 位元組的欄位排列（與 [`sm_gn_chart_tool.py`](sm_gn_chart_tool.py) 中 `_header_layout == "sdom"` 一致）。
更廣的 SM／GN 譜面格式見 [`SM_GN_NOTE_FORMAT.md`](SM_GN_NOTE_FORMAT.md)。

---

## 1. 檔案外層

若檔案開頭為魔數 `ddrm`（小端 `0x6D726464`），前 **0x54** 為 ddrm 包裝，**其後**才是 StepFile 本文。工具函式：`gn_extract_body()`。

---

## 2. 表頭總覽

| 位元組範圍 | 內容 |
|------------|------|
| 0–299 | 表頭固定長度；之後接三難度 StepFrame 串流 |
| 譜面區間 | `address_easy`…`address_normal`（Easy）、`address_normal`…`address_hard`（Normal）、`address_hard`…`address_end`（Hard） |

- 全檔 **little-endian**。
- 本工具要求 **`address_easy == 300`**（表頭結束即譜面起點）。

---

## 3. 欄位對照表（SDOM）

| 偏移 | 長度 | 型別 | `StepFileBin` 屬性（SDOM） | 說明 |
|------|------|------|---------------------------|------|
| 0 | 4 | `int32` | `file_id` | 譜面／檔案識別 |
| 4 | 4 | ASCII + `\0` | `file_type` | 常為 `gn`，填滿 4 bytes |
| 8 | 4 | `float` | 無單獨屬性名（併入 `header_prefix8`） | 反編譯：`FUN_00519140((double)(x * 10.0 - 0.9))`，**x**＝此 float（表頭載入後在 **`this+0x410`**）。|
| 12 | 4 | `int32` | 同上（`header_prefix8` 後 4 bytes） | 欄位語意未知；範例 **`de_sdom1219K.gn`** 為 **10**（`this+0x414`）。 |
| 16 | 4 | `float` | `bpm` | 譜面 **BPM**；客戶端用於捲動／時間換算（見 §7） |
| 20 | 6 | 3×`int16` | `level_easy`, `level_normal`, `level_hard` | 三難度級數 |
| 26 | 2 | `int16` | `header_padding26` | 常為 0 |
| 28 | 12 | 6×`int16` | `header_mid_int16` | 引擎用數值；語意未於本工具內固定 |
| 40 | 12 | 3×`int32` | `note_count_easy`, `note_count_normal`, `note_count_hard` | 三難度「表頭統計」音符計（如 315／706／1061）；與 §7 內用於索引表大小的欄位不同 |
| 52 | 12 | 3×`int32` | `extra52_easy`, `extra52_normal`, `extra52_hard` | 客戶端載入時以 **`+0x43c + difficulty*4`** 讀出，作 **`(n+1)*0x88`／`(n+1)*0x1c`** 查詢表長度之 **n**（須小於 **501**，即 `0x1f5`）；實檔常為較小整數（如 86） |
| 64 | 12 | 3×`int32` | `measurements_*` | 與譜面時間軸相關之 measurement 欄位 |
| 76 | 32 | bytes | `raw_unknown_string0` | |
| 108 | 32 | bytes | `raw_title` | UTF-8，`\0` 截斷 |
| 140 | 32 | bytes | `raw_unknown_string1` | |
| 172 | 32 | bytes | `raw_writer` | UTF-8 |
| 204 | 32 | bytes | `raw_producer` | UTF-8 |
| 236 | 32 | bytes | `raw_file_name` | UTF-8 |
| 268 | 4 | `int32` | `unknown19` | |
| 272 | 12 | 3×`int32` | `duration_easy`, `duration_normal`, `duration_hard` | |
| 284 | 16 | 4×`uint32` | `address_easy`, `address_normal`, `address_hard`, `address_end` | 譜面區在檔內的 byte 偏移 |

### 3.1 範例（offset 8–15 連續 8 bytes）

`9a9939400a000000`：小端 float ≈ **2.9**（offset 8），接 int32 **10**（offset 12）。表頭基底對物件 **`this+0x408`** 之對齊見上表 offset **16** 列（BPM 在 **`this+0x418`**）。

---

## 4. 與 legacy 表頭的差異

部分舊版／Arrowgene 式檔案前段為 **16×`int16`（offset 8）＋offset 40 的 `level_*`、46 的 `post_level_h`、52 的 `note_count_*`**。
`StepFileBin` 會以 **`stepfile_header_sdom_summary()`** 的啟發式判斷是否為 SDOM；不符合時改走 **legacy** 解析與寫回（`_header_layout == "legacy"`）。

---

## 5. 相關工具

| 工具 / 符號 | 用途 |
|-------------|------|
| [`stepfile_header_extract.py`](stepfile_header_extract.py) | 從含 ddrm 或純 StepFile 切出前 300 bytes |
| [`stepfile_header_print.py`](stepfile_header_print.py) | 列印表頭摘要與欄位 |
| `stepfile_header_sdom_summary` | 僅對前 300 bytes 做 SDOM 啟發式檢測（BPM／級數／音符計範圍） |

---

## 6. 範例（`de_sdom1219K` 略過 ddrm 後表頭）

以下僅供對照位元組是否合理，不同曲目數值會不同。

| 欄位 | 範例值 |
|------|--------|
| `file_id` | 11219 |
| `file_type` | `gn` |
| `bpm` | 163.0 |
| `level_easy` / `normal` / `hard` | 4 / 10 / 14 |
| `note_count_*` | 315 / 706 / 1061 |
| `extra52_*` | 86 / 86 / 86 |
| `address_easy` | 300 |

---

## 7. 與 `sdo_stand_alone.exe.c`（`CNewNote::LoadNote`）片段對照

以下對應 [`sdo_stand_alone.exe.c`](../test/sdo_stand_alone.exe.c) 約 **104313–104366** 行（表頭已讀入 **`param_1+0x408`**、`file_type` 已驗為 `gn` 之後）：

| 你的分析／本文件 | 反編譯摘要 |
|------------------|------------|
| 表頭固定 **300** bytes | `FUN_0040f450(..., 300)`；註解已標「表頭固定 300」 |
| **`note_count_*`（檔案 +40）** | 物件 **`+0x430 + difficulty*4`**（`0x408+0x28`）一帶用於與 **measurement／區間上限** 相關的比較迴圈（見同檔約 **103368–103382** 等） |
| **`extra52_*`（檔案 +52）** | 片段以 **`*(uint*)(param_1 + 0x43c + difficulty*4)`** 讀出（`0x408+0x34`），**必須小於 501** 才配置 **`(n+1)*0x88`** 主表（`+0x548`）與 **`(n+1)*0x1c`** 輔表（`+0x467c`）；註解稱「note_count」係客戶端**索引表維度**之 n，**與檔案 +40 的「表頭統計音符數」不是同一個 int32** |
| **BPM（檔案 +16）** | **`*(float*)(param_1+0x418)`** 用於捲動／時間（`FUN_0048db40` 等） |
| **offset +8 之 float** | **`*(float*)(param_1+0x410)`** 搭配 `*10.0-0.9` 呼叫 `FUN_00519140`（與 BPM 分列） |
| **StepFrame** | 註解：`+0x448 + difficulty*4` 為該難度區段內 **StepFrame 筆數**；每筆為 `measurement`（`int32`）、`stepFrameType`（`int16`）、`interval`（`uint16`）、`interval*4` 的 slot 資料，與 [`SM_GN_NOTE_FORMAT.md`](SM_GN_NOTE_FORMAT.md) 一致 |

---

## 8. StepFrame Type 類型說明

除了已知對應方向鍵的 **`2`（左）、`3`（下）、`4`（上）、`5`（右）**（與 [`sm_gn_chart_tool.py`](sm_gn_chart_tool.py) 之 `COL_TO_FRAME_TYPE = (2, 3, 4, 5)`／[`gn_sm_chart_tool.py`](gn_sm_chart_tool.py) 之 `TYPE_TO_COL` 一致）之外，實際檔案（如 `de_sdom1305K.gn`）經分析還有以下特殊的控制用 `step_frame_type`：

| `step_frame_type` | 用途 | 資料格式與特徵 |
|-------------------|------|----------------|
| **1** | **BPM（變速）** | slot 內的 4 個 bytes (`u0`(2 bytes) + `u1`(1 byte) + `step_note_type`(1 byte)) 會被**合併解讀為一個 IEEE 754 32-bit `float` 浮點數**。若該浮點數非 0（例如 `170.0`），即代表在該 `interval` 的對應拍點上改變 BPM。 |
| **9** | **小節標記** | 用於定義小節（Measure / Bar line）。觀察到每個小節其 `interval` 多為 4，並於第一個 slot 的 `u0` 欄位存有與 measurement 相關的遞增整數（例如 `u0 = 84`）。 |
| **10** | **音樂開始與結束** | 用於標記音樂段落的頭尾。例如在譜面的頭與尾的 measurement 會看到 `u0 = 1000` (或 998 等) 的標記，告訴引擎歌曲進度的起止。 |

> [!NOTE]
> 如果你在看譜工具中看到 `type 1` 的 slot 第一項（`u0`）為 `0`，那是因為如 `340.0` 這種浮點數的 Hex 表示（`0x00 0x00 0xAA 0x43`），在 Little-Endian 的前 2 個 bytes 剛好是 `0x00 0x00`。請務必將 4 個 bytes 視為一個完整的 float！

---

*若與實機或更新版引擎不一致，以實際二進位檔與 `sm_gn_chart_tool.py` 行為為準。*
