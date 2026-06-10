# SDM 解密流程說明

本文件整理目前專案中 **兩種** 將《熱舞 Online》`.sdm` 轉成可播放 `.ogg` 的方式，以及我們如何透過 **有密鑰解碼** 驗證標頭、再推導出 **無密鑰解碼** 的完整分析流程。

---

## 一、SDM 檔案結構

| 區段 | 位元組範圍 | 加密方式 | 需要 songencode.dat？ |
|------|------------|----------|:--------------------:|
| 棄用位元 | `byte[0]` | 無 | 否 |
| DES 加密標頭區 | `byte[1:513]` | 需與 `songencode.dat` 的 prefix 合併後 DES 解密 | **是** |
| 音訊主體（混淆） | `byte[513:]` | 僅做 `(505 - byte) % 256` | **否** |

有密鑰路徑會還原完整的 OGG；無密鑰路徑則利用「多首歌曲共用同一套 Vorbis 編碼參數」的事實，用通用標頭模板補齊前段。

---

## 二、階段一：有密鑰解碼（驗證用）

### 2.1 依賴檔案

- `sdm_decryptor.py`：實作與遊戲相同的 DES（`GameDES`），供批次解碼器使用。
- `sdm_batch_decoder.py`：讀取 `songencode.dat`，依 OGG 序列號配對金鑰並輸出 `.ogg`。
- `songencode.dat`：須放在與 `.sdm` 可對應的路徑（例如 `DanceOnline\music\songencode.dat`）。

### 2.2 指令範例

**單首：**

```powershell
python sdm_batch_decoder.py DanceOnline\music\sdom0001.sdm DanceOnline\music\songencode.dat -o decrypted_music
```

**整個資料夾：**

```powershell
python sdm_batch_decoder.py DanceOnline\music DanceOnline\music\songencode.dat -o decrypted_music
```

---

## 三、分析過程

### 3.1 問題背景：SDM 缺少什麼？為什麼不能直接播放？

SDM 檔案的 **byte[1:513]**（512 bytes）是經過 DES 加密的，必須與 `songencode.dat` 內對應歌曲的 **prefix**（512 bytes）合併後才能解密，還原出 OGG 的前 1024 bytes。

**OGG 前 1024 bytes 包含的關鍵資訊：**

| 內容 | 說明 | 缺少會怎樣？ |
|------|------|-------------|
| Page 0 - Identification Header | channels、sample_rate、bitrate、blocksize | 播放器不知道音訊格式，無法初始化解碼器 |
| Page 1 - Comment Header | 編碼器資訊、曲目標籤 | 無法讀取 metadata |
| Page 2 - Setup Header (Codebooks) | Vorbis 解碼所需的頻率表/碼書 | **致命！沒有 codebook 完全無法解碼音訊** |

**簡單來說：**
- SDM 尾部 `byte[513:]` 只做簡單的 `(505 - b) % 256` 混淆，可以輕鬆還原
- 但還原後只有「音訊資料頁」，缺少「說明書」（前三個 header pages）
- 播放器拿到一堆 Vorbis packet 卻不知道怎麼解碼 → 無法播放

**傳統解法：** 必須有 `songencode.dat` 提供 DES 金鑰和 prefix，才能還原完整 OGG。

**我們的目標：** 分析這些 header 是否有共通性，能否用「通用模板」取代 `songencode.dat`？

### 3.2 分析方法

使用有密鑰方式解出 `sdom1140` ~ `sdom1147` 共 8 首歌，比對它們的 OGG 標頭結構，判斷是否存在「共通性」足以繞過 `songencode.dat`。

### 3.3 OGG Vorbis 標頭結構

一個合法的 OGG Vorbis 檔案開頭有三個必要的 Header Page：

| 頁面 | 內容 | 說明 |
|------|------|------|
| Page 0 | Identification Header | 包含 channels、sample_rate、bitrate、blocksize 等編碼參數 |
| Page 1 | Comment Header | 編碼器標籤（vendor string）、曲目資訊 |
| Page 2 | Setup Header (Codebooks) | 解碼所需的頻率表，**沒有它播放器無法還原音訊** |

### 3.4 分析結果：8 首 OGG 的前 1024 bytes 共通性極高

#### Page 0 — Vorbis Identification (byte 0–57，共 58 bytes)

**OGG Page Header (28 bytes):**

| 欄位 | 大小 | 8 首全部一樣？ |
|------|:----:|:--------------:|
| `OggS` 魔術字 | 4B | 完全一致 |
| version | 1B | 完全一致 (0) |
| header_type | 1B | 完全一致 (0x02 = BOS) |
| granule_position | 8B | 完全一致 (0) |
| **serial_number** | 4B | **每首不同** |
| page_sequence_number | 4B | 完全一致 (0) |
| **CRC-32** | 4B | **每首不同** |
| num_segments | 1B | 完全一致 (1) |
| segment_table | 1B | 完全一致 ([30]) |

**Vorbis Identification Body (30 bytes):**

| 欄位 | 大小 | 8 首全部一樣？ |
|------|:----:|:--------------:|
| packet_type | 1B | 完全一致 (0x01) |
| "vorbis" 標識 | 6B | 完全一致 |
| vorbis_version | 4B | 完全一致 (0) |
| channels | 1B | 完全一致 (2) |
| sample_rate | 4B | 完全一致 (44100) |
| bitrate_maximum | 4B | 完全一致 (-1) |
| bitrate_nominal | 4B | 完全一致 (128031) |
| bitrate_minimum | 4B | 完全一致 (-1) |
| blocksize_0 / blocksize_1 | 1B | 完全一致 (0xB8 = 256/2048) |
| framing_flag | 1B | 完全一致 (1) |

#### Page 1 — Vorbis Comment (byte 58–2885，共 2828 bytes)

**OGG Page Header (39 bytes):**

| 欄位 | 大小 | 8 首全部一樣？ |
|------|:----:|:--------------:|
| `OggS` 魔術字 | 4B | 完全一致 |
| version | 1B | 完全一致 (0) |
| header_type | 1B | 完全一致 (0x00) |
| granule_position | 8B | 完全一致 (0) |
| **serial_number** | 4B | **每首不同** |
| page_sequence_number | 4B | 完全一致 (1) |
| **CRC-32** | 4B | **每首不同** |
| num_segments | 1B | 完全一致 |
| segment_table | 11B | 完全一致 |

**Vorbis Comment Body (2789 bytes):**

| 欄位 | 大小 | 8 首全部一樣？ |
|------|:----:|:--------------:|
| packet_type | 1B | 完全一致 (0x03) |
| "vorbis" 標識 | 6B | 完全一致 |
| vendor_length | 4B | 完全一致 (32) |
| vendor_string | 32B | 完全一致 (`Xiphophorus libVorbis I 20020408`) |
| user_comment_list_length | 4B | 完全一致 (1) |
| comment + codebook 開頭 | 2742B | 完全一致 |

#### Page 2 — Vorbis Setup / Codebooks

- **起始位置 = byte 2886**
- **完全在 tail 區（byte 1024+）**
- **不需要密鑰就能用 `(505 - b) % 256` 解出！**

### 3.5 整體統計

```
不同位元組位置數: 12 / 1024
相同位元組位置數: 1012 / 1024
相同率: 98.8%

Page 0: bytes [0-58)   = 58B,   差異 6 個 (serial 2B + CRC 4B)
Page 1: bytes [58-2886) = 2828B, 差異 6 個 (serial 2B + CRC 4B)
```

**僅 12 bytes 不同** = Page 0 的 serial(2B)+CRC(4B) + Page 1 的 serial(2B)+CRC(4B)


> 
> - **Serial Number**：從 SDM 尾部（byte 513+）用 `(505 - b) % 256` 解開後，掃描找到第一個 `OggS` 頁面，讀取 offset 14-17 的 4 bytes 即為該首歌的 serial。（實測這些歌的 serial 值都不大，高 2 bytes 為 0，所以只有低 2 bytes 實際不同）
> 
> - **CRC-32**：OGG 規格定義的校驗碼，使用多項式 `0x04C11DB7`。計算方式是將整個 page 的 CRC 欄位先填 0，對整個 page bytes 做 CRC 運算即可得出。
> 
> 因此，只要有「通用模板」+「從尾部取得的 serial」，就能重算出正確的 CRC，不需要 `songencode.dat`。

### 3.6 關鍵發現：DES 加密邊界 vs OGG 頁面邊界

```
DES 加密範圍: byte 0-1023 (需要 songencode.dat)
Tail 可解範圍: byte 1024+  (只需 505 變換)

Page 0: [0-58)     全部在 DES 加密區
Page 1: [58-2886)  跨界! DES=58-1023, tail=1024-2885
Page 2: [2886-...)  完全在 tail 區 → 不需要密鑰!
```

**Setup/Codebooks（Page 2）完全落在不需要密鑰的尾段！**

### 3.7 假設沒有 songencode.dat，暴力猜測需要猜對什麼？

既然 Codebook 完全在 tail 區不需要密鑰，那 DES 加密區（Page 0 + Page 1 前半）的內容能否暴力猜測？

#### Page 0 — Vorbis Identification (30 bytes body)

| 參數 | 大小 | 需要猜？ | 可能值 | 難度 |
|------|:----:|:--------:|--------|:----:|
| packet_type | 1B | 否 | 固定 0x01 | - |
| "vorbis" | 6B | 否 | 固定 | - |
| vorbis_version | 4B | 否 | 固定 0 | - |
| **channels** | 1B | **是** | 1 或 2 | 低 |
| **sample_rate** | 4B | **是** | 22050 / 44100 / 48000 等 | 中 |
| bitrate_max/min | 8B | 否 | 通常 -1，不影響解碼 | - |
| bitrate_nominal | 4B | 否 | metadata，不影響解碼 | - |
| **blocksize** | 1B | **是** | 影響解碼窗口，常見 0xB8 | 中 |
| framing_flag | 1B | 否 | 固定 1 | - |

#### Page 1 — Vorbis Comment (2789 bytes body)

| 參數 | 大小 | 需要猜？ | 說明 | 難度 |
|------|:----:|:--------:|------|:----:|
| packet_type | 1B | 否 | 固定 0x03 | - |
| "vorbis" | 6B | 否 | 固定 | - |
| **vendor_length** | 4B | **是** | 編碼器名稱長度 | 高 |
| **vendor_string** | ?B | **是** | 編碼器名稱，**長度可變！** | 高 |
| comment_count | 4B | 否 | 可以填 0 | - |
| comments | ?B | 否 | 可以為空 | - |

#### Page 2 — Codebook

| 內容 | 需要猜？ | 說明 |
|------|:--------:|------|
| 整個 Codebook | **否！** | 完全在 tail 區，直接解出來 |

#### 最大難點：Page 1 長度問題

```
Page 1 位置: byte 58 ~ 2885 (共 2828 bytes)
其中:
  - byte 58-1023  (966B)  → DES 加密區，要猜
  - byte 1024-2885 (1862B) → tail 區，可直接解
```

**如果猜錯 vendor_string 長度 → Page 1 總長度錯 → Page 2 起始位置錯 → 整個 OGG 結構崩壞！**

#### 暴力猜測的搜索空間

| 參數 | 常見選項數 |
|------|:---------:|
| channels | 2（1 或 2） |
| sample_rate | ~5（22050, 32000, 44100, 48000...） |
| blocksize | ~3（常見組合） |
| vendor_string | **數百種可能**（不同編碼器版本） |

**總組合：** 2 × 5 × 3 × 數百 = **數千種組合**

每次嘗試都要重算 CRC、拼接、用播放器測試是否能播...

#### 結論

- **Codebook 不用猜** — 最複雜的部分在 tail 區，這是最大的幸運
- **channels / sample_rate / blocksize** — 搜索空間有限，暴力枚舉可行
- **vendor_string** — 這是最大障礙，長度可變且影響整個結構對齊

**理論上可以暴力猜測**，但用 `songencode.dat` 解一首的優勢是：**一次就確定所有參數，之後永久通用**。這就是為什麼我們的分析流程是「先解幾首 → 確認共通性 → 建立模板」。

### 3.8 結論：可以不用 songencode.dat 解出完整可播放 OGG

因為所有歌曲都用**同一個編碼器**（`Xiphophorus libVorbis I 20020408`）、**同一組參數**（2ch / 44100Hz / 128kbps）編碼，所以標頭結構**完全相同**。

**無密鑰解密步驟：**

1. 從任一已解密 OGG 提取 **byte 0–2885 作為通用模板**（Page 0 + Page 1）
2. 用 `(505 - b) % 256` 解開 SDM 尾部，從中掃出 **serial number**
3. 把模板裡的 serial 欄位換成該首歌的 serial，**重算兩頁的 CRC**
4. 拼接：**模板 + tail 中的 Page 2（Codebooks）+ 音訊資料頁** → 完整 OGG

### 3.9 驗證結果

將無密鑰輸出與有密鑰輸出逐位元組比對：

| 檔案 | 大小 | 比對結果 |
|------|------|:--------:|
| sdom1140.ogg | 2,125,425 B | **IDENTICAL** |
| sdom1141.ogg | 2,178,744 B | **IDENTICAL** |
| sdom1142.ogg | 1,831,458 B | **IDENTICAL** |
| sdom1143.ogg | 1,844,518 B | **IDENTICAL** |
| sdom1144.ogg | 1,672,392 B | **IDENTICAL** |
| sdom1145.ogg | 2,960,015 B | **IDENTICAL** |
| sdom1146.ogg | 1,759,069 B | **IDENTICAL** |
| sdom1147.ogg | 2,345,431 B | **IDENTICAL** |

**8 首全部 bit-identical（逐位元組完全一致）**，證明無密鑰方式與有密鑰方式輸出結果相同。

---

## 四、階段二：無密鑰解碼（日常使用）

### 4.1 原理摘要

- 使用一份預先準備的 **通用標頭模板**（Page 0 + Page 1，共 2886 bytes），內容來自已正確解密的 OGG（serial/CRC 在模板內先清零，程式內重算）。
- 從 `.sdm` 的 **尾段** `transform_505` 後掃出第一個 `OggS` 取得 **serial**。
- 對 Page 0、Page 1 填入正確 serial 並依 OGG 規格重算 **CRC-32**（多項式 `0x04C11DB7`）。
- 再與尾段資料拼接，得到完整、可播放的 `.ogg`。

### 4.2 必要檔案

- `bms_sdo/sdm_decoder.py`：無密鑰解碼主程式。
- `header_template.b64`：位於 `bms_sdo/header_templates/`。內容為 2886 bytes 的 Base64。

若缺少 `header_template.b64`，請先依「二、階段一」用 `songencode.dat` 解出**任一首**合法 `.ogg`，再將前 2886 bytes 編成 Base64 存成 `header_template.b64`。

### 4.3 使用方式

**解單首：**

```powershell
cd tools
python -m bms_sdo.sdm_decoder DanceOnline\music\sdom0001.sdm -o output
```

**解整個資料夾：**

```powershell
cd tools
python -m bms_sdo.sdm_decoder DanceOnline\music -o output
```

- 第一個參數：單一 `.sdm` 路徑，或內含多個 `.sdm` 的資料夾。
- `-o` / `--output_dir`：輸出目錄（預設為 `decrypted_keyless`）。

**不需要** `songencode.dat`、`sdm_decryptor.py`。

---

## 五、兩種方式對照

| 項目 | 有密鑰（`sdm_batch_decoder.py`） | 無密鑰（`bms_sdo.sdm_decoder`） |
|------|-----------------------------------|-------------------------------------|
| 需要 `songencode.dat` | 是 | 否 |
| 需要 `sdm_decryptor.py` | 是 | 否 |
| 需要 `header_template.b64` | 否 | 是（與腳本同目錄） |
| 適用情境 | 金鑰檔齊全、需與遊戲完全一致驗證 | 大量批次、僅有 `.sdm` 時 |
| 輸出結果 | 完整 OGG | 完整 OGG（與有密鑰 bit-identical） |

---

## 六、相關檔案一覽

| 檔案 | 用途 |
|------|------|
| `sdm_decryptor.py` | DES 解密（有密鑰路徑） |
| `sdm_batch_decoder.py` | 依 `songencode.dat` 批次 SDM → OGG |
| `bms_sdo.sdm_decoder` | 不依 `songencode.dat` 的 SDM → OGG |
| `header_template.b64` | 無密鑰路徑所需之標頭模板 |
| `compare_ogg_headers.py` | 多檔 OGG 前段標頭比對（分析用） |
| `export_sdm_headers_csv.py` | 解碼並匯出標頭欄位 CSV（分析用） |
| `sdm_no_key_analysis.py` | 不用密鑰分析 SDM 尾段資訊（分析用） |

---

## 七、環境

- Python 3.10+ 建議（腳本使用 `int | None` 等語法時需 3.10 以上）。
- 驗證輸出可用 VLC、ffprobe 或一般支援 OGG/Vorbis 的播放器。

---

## 八、實測成果

| 資料夾 | SDM 數量 | 解碼成功 |
|--------|----------|:--------:|
| `DanceOnline\music` | 228 首 | 228 首 (100%) |
| `熱舞 Online\Music` | 322 首 | 322 首 (100%) |
| **合計** | **550 首** | **550 首 (100%)** |

全部使用無密鑰解碼器完成，零失敗。
