# SongList.dat 格式說明

本文件說明 **Super Dancer Online（SDO／超級舞者）** 系列遊戲的 `SongList.dat` 歌曲列表檔案格式。

## 概述

`SongList.dat` 是遊戲用來管理歌曲資訊的二進位檔案，包含歌曲 ID、檔案路徑、歌名、難度等資訊。

## 整體結構

```
┌─────────────────────────────────────┐
│           Header (2+ bytes)         │
├─────────────────────────────────────┤
│           Record 0                  │
├─────────────────────────────────────┤
│           Record 1                  │
├─────────────────────────────────────┤
│              ...                    │
├─────────────────────────────────────┤
│           Record N-1                │
└─────────────────────────────────────┘
```

### Header 結構

| 偏移 | 大小 | 類型 | 說明 |
|------|------|------|------|
| 0x00 | 2 | uint16 LE | 記錄數量 (N) |
| 0x02 | 變動 | bytes | 填充直到第一條記錄（偵測方式見下方） |

**Header 大小偵測**：
- 預設 header 大小為 2 bytes
- 若檔案內容中找到 `sdom` 字串，則 header 結束於該字串位置（即 `sdom` 之前都是 header）
- 計算公式：`rec_size = (file_size - header_size) / count`

---

## 記錄格式（兩種版本）

根據不同版本的客戶端，記錄大小分為 **752 bytes** 與 **756 bytes** 兩種。

### 版本識別

| 記錄大小 | 關鍵偏移 fo | 名稱偏移 no | 常見於 |
|----------|-------------|-------------|--------|
| 752 bytes | 452 | 560 | 單機版 |
| 756 bytes | 456 | 564 | 台灣版、熱舞 Online |

---

## 756 Bytes 記錄格式（完整版）

此格式常見於**台灣版、熱舞 Online** 等線上版本，包含完整資源路徑與 GN 對應統計欄位。

### 756 bytes 單一總表（偏移採十進位，範圍 0-755）

| 偏移 | 大小 | 類型 | 欄位名稱 | 說明 |
|------|------|------|----------|------|
| 0 | 32 | string | **gn_path** | GN 譜面檔路徑（如 `sdom0001K.gn`） |
| 32 | 32 | string | **dps_path** | DPS 特效檔路徑（如 `10001.dps`） |
| 64 | 32 | string | **png_cover1** | 封面圖片路徑 1（如 `10001.png`） |
| 96 | 32 | string | **png_cover2** | 封面圖片路徑 2 |
| 128 | 32 | string | **png_cover3** | 封面圖片路徑 3 |
| 160 | 32 | string | **ogg_path** | OGG 音樂檔路徑（如 `10001.ogg`） |
| 192 | 4 | uint32 LE | unk_192 | 未知（常為 1） |
| 196 | 4 | uint32 LE | unk_196 | 未知（常為 1） |
| 200 | 252 | bytes | reserved_200_451 | 保留區域（用途未明） |
| 452 | 4 | uint32 LE | unk_fo | 未知（常為 1） |
| 456 | 4 | uint32 LE | **file_id** | 歌曲檔案 ID（如 10001） |
| 460 | 4 | uint32 LE | exper_id | 經驗值 ID |
| 464 | 4 | float32 LE | float1 | 未知浮點數（常約 2.90） |
| 468 | 4 | uint32 LE | unk1 | 未知整數 |
| 472 | 4 | float32 LE | **bpm** | BPM |
| 476 | 2 | uint16 LE | **easy_diff** | Easy 難度等級 |
| 478 | 2 | uint16 LE | **normal_diff** | Normal 難度等級 |
| 480 | 2 | uint16 LE | **hard_diff** | Hard 難度等級 |
| 482 | 2 | bytes | padding | 填充 |
| 484 | 4 | uint32 LE | **total_easy** | Easy 所有 note 總數（含 type 9/10） |
| 488 | 4 | uint32 LE | **total_normal** | Normal 總數 |
| 492 | 4 | uint32 LE | **total_hard** | Hard 總數 |
| 496 | 4 | uint32 LE | **note_cnt_easy** | Easy LDUR 音符數（= GN note_count） |
| 500 | 4 | uint32 LE | **note_cnt_normal** | Normal LDUR 音符數 |
| 504 | 4 | uint32 LE | **note_cnt_hard** | Hard LDUR 音符數 |
| 508 | 4 | uint32 LE | **max_measure_easy** | Easy 最大 measurement |
| 512 | 4 | uint32 LE | **max_measure_normal** | Normal 最大 measurement |
| 516 | 4 | uint32 LE | **max_measure_hard** | Hard 最大 measurement |
| 520 | 4 | uint32 LE | **measure_easy** | Easy 小節數（= GN measurements） |
| 524 | 4 | uint32 LE | **measure_normal** | Normal 小節數 |
| 528 | 4 | uint32 LE | **measure_hard** | Hard 小節數 |
| 564 | 64 | string | **song_name** | 歌曲名稱 |
| 628 | 32 | string | artist | 歌手/演唱者 |
| 660 | 32 | string | producer | 製作人 |
| 692 | 32 | string | gm_path | GM 動作檔路徑 |
| 724 | 4 | uint32 LE | gn_header_size | GN StepFile 表頭大小（固定 300） |
| 728 | 12 | uint32 LE ×3 | **duration_easy, duration_normal, duration_hard** | 三難度 duration（秒） |
| 740 | 16 | uint32 LE ×4 | **address_easy, address_normal, address_hard, address_end** | 三難度資料區起點與結尾位址（對應 GN 表頭 offset 284） |

> 註 1：`duration_*` 已以 `SongList.dat` 實檔交叉驗證（例如 2954/2955/2956 對應 148/172/154 秒）。
>
> 註 2：`gn_header_size` 與 `address_*` 參考 `doc/SM_GN_NOTE_FORMAT.md` 的 GN/StepFile 表頭定義：`address_easy` 正常為 300，後續位址對應三難度區段邊界與 `address_end`。

### GN 相關數據（以 456 為 fo 實測對照）

這些欄位與 GN 譜面表頭中的欄位對應（參見 [SM_GN_NOTE_FORMAT.md](SM_GN_NOTE_FORMAT.md)）。

**實測對照範例**（file_id=12953, sdom2953K.gn）：
- GN 表頭 note_count: 664, 935, 1039
- GN 表頭 measurements: 347, 349, 349
- Songlist total: 706, 976, 1080（含所有 type）
- Songlist note_cnt: 664, 935, 1039 ✓（只計 LDUR）
- Songlist measure: 347, 349, 349 ✓

**note 計數說明**：
- **total** = 所有 `step_frame_type` 的 note 總數（含 type 9 小節線、type 10 音樂起止）
- **note_cnt** = 只計算 type 2, 3, 4, 5（LDUR 四方向）的 note（= GN 表頭 note_count）

| 偏移 | 欄位名稱 | 說明 |
|------|----------|------|
| 484 | **total_easy** | **所有 note 總數（含 type 9 小節線、type 10 音樂起止）** |
| 488 | **total_normal** | **Normal 總數** |
| 492 | **total_hard** | **Hard 總數** |
| 496 | **note_cnt_easy** | **Easy LDUR 音符數（= GN 表頭 [40] note_count）** |
| 500 | **note_cnt_normal** | **Normal LDUR 音符數** |
| 504 | **note_cnt_hard** | **Hard LDUR 音符數** |
| 508 | **max_measure_easy** | **Easy 最大 measurement（譜面時間範圍）** |
| 512 | **max_measure_normal** | **Normal 最大 measurement** |
| 516 | **max_measure_hard** | **Hard 最大 measurement — 三個難度通常相同** |
| 520 | **measure_easy** | **Easy 小節數（= GN 表頭 [64] measurements）** |
| 524 | **measure_normal** | **Normal 小節數** |
| 528 | **measure_hard** | **Hard 小節數** |

#### `max_measure` vs `measure` 差異說明

| 欄位 | 範例值 | 意義 |
|------|--------|------|
| **measure** | 347, 349, 349 | **StepFrame 總數量**（譜面資料的 frame 筆數） |
| **max_measure** | 79, 79, 79 | **最大小節編號**（譜面時間軸的終點） |

同一個 measurement（小節）可以有多個 StepFrame（L/D/U/R 各一個 + type 9 小節線 + type 10 音樂起止），所以 `measure` (347) > `max_measure` (79)。

#### `max_measure` vs `type 9 小節線數量` 差異說明

| 欄位 | 範例值 | 意義 |
|------|--------|------|
| **max_measure** | 79 | 譜面時間軸終點（最大小節編號） |
| **type 9 數量** | 41 | 實際放置的小節線數量 |

小節線（type 9）只放在**奇數** measurement 位置（1, 3, 5, 7...），偶數位置沒有小節線，所以 `type 9 數量 ≈ max_measure / 2`。

> 上表偏移採十進位。對照：`fo=456`、`no=564`（756 bytes 格式）。

---

## 752 Bytes 記錄格式（簡化版）

此格式常見於**單機版**，欄位較少。

### 主要欄位

| 偏移 | 大小 | 類型 | 欄位名稱 | 說明 |
|------|------|------|----------|------|
| 0x000 | 32 | string | gn_path | GN 檔案路徑 |
| 0x020 | 128 | bytes | reserved_1 | 保留區域（不含 dps/png 路徑） |
| 0x0A0 | 32 | string | ogg_path | OGG 音樂檔案路徑 |
| 0x0C0 | 260 | bytes | reserved_2 | 保留區域 |
| **0x1C4** | 4 | uint32 LE | **file_id** | 歌曲檔案 ID |
| 0x1C8 | 4 | uint32 LE | exper_id | 經驗值 ID |
| 0x1CC | 12 | bytes | reserved_3 | 保留區域 |
| **0x1D8** | 2 | uint16 LE | **easy_diff** | Easy 難度等級 |
| 0x1DA | 2 | uint16 LE | normal_diff | Normal 難度等級 |
| 0x1DC | 2 | uint16 LE | hard_diff | Hard 難度等級 |
| 0x1DE | 82 | bytes | reserved_4 | 保留區域 |
| **0x230** | 64 | string | **song_name** | 歌曲名稱 |
| 0x270 | 32 | string | artist | 歌手/演唱者 |
| 0x290 | 32 | string | producer | 製作人 |
| 0x2B0 | 32 | string | gm_path | GM 檔案路徑 |

> 偏移以十六進位表示，fo=452=0x1C4，no=560=0x230

---

## 兩種格式對照表

| 欄位 | 752 bytes 偏移 | 756 bytes 偏移 | 說明 |
|------|---------------|---------------|------|
| gn_path | 0 | 0 | 相同 |
| dps_path | ❌ 無 | 32 | **756 版專有** |
| png_cover1 | ❌ 無 | 64 | **756 版專有** |
| png_cover2 | ❌ 無 | 96 | **756 版專有** |
| png_cover3 | ❌ 無 | 128 | **756 版專有** |
| ogg_path | 160 | 160 | 相同 |
| file_id | 452 | 456 | 偏移+4 |
| exper_id | 456 | 460 | 偏移+4 |
| easy_diff | 472 | 476 | 偏移+4 |
| normal_diff | 474 | 478 | 偏移+4 |
| hard_diff | 476 | 480 | 偏移+4 |
| song_name | 560 | 564 | 偏移+4 |
| artist | 592 | 628 | 偏移不同 |
| producer | 624 | 660 | 偏移不同 |
| gm_path | 656 | 692 | 偏移不同 |

---

## 字串編碼

| 版本 | 編碼 |
|------|------|
| 大陸版 | GB18030 / GBK |
| 台灣版 | Big5 |

字串皆以 **NULL (0x00)** 結尾，未使用空間填充 0x00。

---

## 讀取範例 (Python)

```python
import struct
import os

def read_songlist(path, encoding='gb18030'):
    with open(path, 'rb') as f:
        data = f.read()

    count = struct.unpack('<H', data[:2])[0]

    # 偵測 header 大小
    sdom_pos = data.find(b'sdom')
    header_size = sdom_pos if sdom_pos != -1 else 2

    # 計算記錄大小
    rec_size = (len(data) - header_size) // count

    # 根據記錄大小決定偏移
    if rec_size == 752:
        fo, no = 452, 560
    else:  # 756
        fo, no = 456, 564

    records = []
    for i in range(count):
        start = header_size + i * rec_size
        d = data[start:start + rec_size]

        rec = {
            'gn_path': d[0:32].split(b'\x00')[0].decode(encoding, 'ignore'),
            'ogg_path': d[160:192].split(b'\x00')[0].decode(encoding, 'ignore'),
            'file_id': struct.unpack('<I', d[fo:fo+4])[0],
            'exper_id': struct.unpack('<I', d[fo+4:fo+8])[0],
            'easy': struct.unpack('<H', d[fo+20:fo+22])[0],
            'normal': struct.unpack('<H', d[fo+22:fo+24])[0],
            'hard': struct.unpack('<H', d[fo+24:fo+26])[0],
            'name': d[no:no+64].split(b'\x00')[0].decode(encoding, 'ignore'),
            'artist': d[no+64:no+96].split(b'\x00')[0].decode(encoding, 'ignore'),
            'producer': d[no+96:no+128].split(b'\x00')[0].decode(encoding, 'ignore'),
            'gm_path': d[no+128:no+160].split(b'\x00')[0].decode(encoding, 'ignore'),
        }
        records.append(rec)

    return records
```

---

## 寫入範例 (Python)

```python
import struct

def write_songlist(path, records, rec_size=752, encoding='gb18030'):
    header_size = 2  # 最小 header

    if rec_size == 752:
        fo, no = 452, 560
    else:
        fo, no = 456, 564

    def write_string(buf, offset, text, length):
        encoded = text.encode(encoding, 'ignore')[:length-1]
        buf[offset:offset+length] = b'\x00' * length
        buf[offset:offset+len(encoded)] = encoded

    with open(path, 'wb') as f:
        f.write(struct.pack('<H', len(records)))

        for rec in records:
            buf = bytearray(rec_size)

            write_string(buf, 0, rec['gn_path'], 32)
            write_string(buf, 160, rec['ogg_path'], 32)

            struct.pack_into('<I', buf, fo, rec['file_id'])
            struct.pack_into('<I', buf, fo+4, rec['exper_id'])
            struct.pack_into('<H', buf, fo+20, rec['easy'])
            struct.pack_into('<H', buf, fo+22, rec['normal'])
            struct.pack_into('<H', buf, fo+24, rec['hard'])

            write_string(buf, no, rec['name'], 64)
            write_string(buf, no+64, rec['artist'], 32)
            write_string(buf, no+96, rec['producer'], 32)
            write_string(buf, no+128, rec['gm_path'], 32)

            f.write(buf)
```

---

## 相關工具

- `tools/songlist_editor.py` — GUI 編輯器，支援讀取、編輯、儲存 SongList.dat
