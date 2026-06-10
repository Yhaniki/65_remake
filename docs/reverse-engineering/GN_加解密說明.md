# .gn 加解密說明（對齊 `sdo_stand_alone.exe` 邏輯）

本文件整理 `FUN_0048e310` / `FUN_004c5f90` 這條載入路徑對 `.gn` 的**驗證**與**解密/加密**方式，並對齊本專案的工具 `gn_crypto_tool.py`（已用真實檔 `sdom1420K.gn` 驗證可通過 CRC 與解密）。

---

## 檔案結構（Header 0x54 bytes）

### 1) 主表頭（明文區，0x00–0x53）

| 偏移 | 長度 | 名稱 | 說明 |
|---:|---:|---|---|
| `0x00` | 4 | `magic` | `0x6D726464`（ASCII `ddrm` 的小端序） |
| `0x04` | 4 | `version` | `0x00000001` |
| `0x08` | 4 | `decl_size` | 檔案宣告大小（通常等於實際檔案大小） |
| `0x0C` | 4 | `seed1` | 用於解密 `block1_ct` 的 LCG 種子 |
| `0x10` | 4 | `crc1` | `crc32(block1_ct)`（注意：是密文區的 CRC） |
| `0x14` | 12 | `reserve` | 保留（通常為 0 或不固定，不參與 `crc1`） |
| `0x20` | 32 | `block1_ct` | 表頭內 32 bytes 的密文區（先驗 CRC、再用 `seed1` 解密） |
| `0x40` | 20 | `tail` | 保留/其他欄位（本工具不依賴） |

> 重點：`crc1` 計算的是 `raw[0x20:0x40]`，不是 `raw[0x14:0x34]`。

### 2) block1 解密後（32 bytes 明文內部欄位）

對 `block1_ct` 用 `seed1` 解密得到 `block1_pt` 後，從 `block1_pt` 內取：

| 偏移（相對 block1_pt） | 長度 | 名稱 | 說明 |
|---:|---:|---|---|
| `0x04` | 4 | `seed2` | 用於解密本文（0x54 之後） |
| `0x08` | 4 | `crc2` | `crc32(body_ct)`，其中 `body_ct = raw[0x54:]`（密文本文的 CRC） |

> 重點：`seed2/crc2` **不在**主表頭固定偏移，而是**藏在 block1 解密後的內容**。

### 3) 本文（Payload，0x54–EOF）

| 偏移 | 名稱 | 說明 |
|---:|---|---|
| `0x54` | `body_ct` | 本文密文（長度 = `len(raw) - 0x54`） |
| `0x54` | `body_pt` | 用 `seed2` 解密後的本文明文 |

---

## CRC 驗證方式

CRC 使用 IEEE CRC32（多項式 `0xEDB88320`），對齊 `FUN_0048bed0`；用 Python 可用：

- `zlib.crc32(data) & 0xFFFFFFFF`

流程：

- **CRC1**：計算 `crc32(raw[0x20:0x40])`，需等於主表頭 `0x10` 的 `crc1`。
- **CRC2**：先解密 block1 得到 `crc2`，再計算 `crc32(raw[0x54:])`，需等於該 `crc2`。

---

## 解密演算法（LCG byte-stream）

解密函式對齊 `FUN_0048bf50`（每 byte 用同一個狀態連續生成 `k_byte`）：

- **更新狀態**：`state = (state * 0x3D09) mod 2^32`
- **取 1 byte key**：`k = (state >> 16) & 0xFF`
- **解密**：`plain = (cipher - k) & 0xFF`
- **加密**：`cipher = (plain + k) & 0xFF`

兩段使用不同種子：

- **block1 解密**：`seed1` 解密 `block1_ct` → `block1_pt`
- **payload 解密**：`seed2` 解密 `body_ct` → `body_pt`

---

## 加密流程（反向生成檔案）

1. 準備 `body_pt`（你要寫進 `.gn` 的明文本文）。
2. 選定 `seed2`，用 LCG **加密**得到 `body_ct`，並計算 `crc2 = crc32(body_ct)`。
3. 準備 `block1_pt`（32 bytes），把 `seed2` 寫入 `block1_pt[0x04:0x08]`、把 `crc2` 寫入 `block1_pt[0x08:0x0C]`。
4. 選定 `seed1`，用 LCG **加密**得到 `block1_ct`，並計算 `crc1 = crc32(block1_ct)`。
5. 組出 0x54 header（`magic/version/decl_size/seed1/crc1/reserve/block1_ct/tail`），再接上 `body_ct`。

---

## 工具使用（`gn_crypto_tool.py`）

### 解密

1. 執行 `python gn_crypto_tool.py`
2. 到「解密」頁籤選擇 `.gn`
3. 選擇輸出目錄
4. 按「解密並寫出」

輸出：

- `*_payload.bin`：本文明文（對應檔案 `0x54` 之後解密結果）
- `*_block1.bin`：`block1_pt`（32 bytes）

### 加密

1. 選擇 `payload.bin`（要加密進 `.gn` 的本文明文）
2. 可選 `block1.bin`（必須 32 bytes；未選則使用全 0，再由工具寫入 seed2/crc2）
3. 填入 `Seed1/Seed2`（十六進位）
4. 選擇輸出 `.gn`
5. 按「加密並寫出」

---

## 常見問題

### 1) 為什麼一開始會出現「表頭 CRC 不符」？

因為 block1 密文區的真實位置是 **`0x20–0x3F`**，而不是 `0x14–0x33`。`0x14–0x1F` 是 12 bytes 保留欄。

### 2) seed2 / crc2 在哪裡？

它們在 **`block1` 解密後的明文**中：

- `seed2 = u32le(block1_pt[0x04:0x08])`
- `crc2  = u32le(block1_pt[0x08:0x0C])`

