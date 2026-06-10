# Datas.sai / Datas.sac 解壓加壓說明

本文件說明 **熱舞 Online / Super Dancer Online** 系列遊戲的資料包格式（`.sai` 索引檔 + `.sac` 資料檔）以及解壓加壓流程。

## 概述

遊戲資源打包為兩個檔案：
- **Datas.sai** — 索引檔（Index），儲存檔案清單與位置資訊（加密）
- **Datas.sac** — 資料檔（Content），儲存實際檔案內容（明文）

可能有多個資料包，如 `Datas0.sac`、`Datas1.sac` 等，對應相同索引或分開索引。

---

## LCG 解密算法

索引檔使用 **線性同餘產生器（Linear Congruential Generator, LCG）** 加密。

### 參數

| 參數 | 值 |
|------|-----|
| 密鑰 (Key) | `0x7C53F961` (即 `\x61\xf9\x53\x7c` little-endian) |
| 乘數 (Multiplier) | `0x3D09` (15625) |
| 模數 (Modulus) | `0x100000000` (2³²) |

### 解密公式

```
key = 0x7C53F961
for i in range(length):
    key = (key * 0x3D09) & 0xFFFFFFFF
    decrypted[i] = (encrypted[i] - ((key >> 16) & 0xFF)) & 0xFF
```

### Python 實作

```python
import struct

def decrypt_data(key_bytes, data):
    """
    LCG 解密算法
    key_bytes: 4 bytes 密鑰 (little-endian)
    data: 加密資料
    """
    length = len(data)
    decrypted = bytearray(length)
    l_key = struct.unpack('<I', key_bytes)[0]
    
    for i in range(length):
        l_key = (l_key * 0x3D09) & 0xFFFFFFFF
        ltmp_key = l_key >> 0x10
        decrypted[i] = (data[i] - (ltmp_key & 0xFF)) & 0xFF
    
    return bytes(decrypted)

# 使用方式
PASSWORD = b'\x61\xf9\x53\x7c'  # 0x7C53F961 LE
decrypted = decrypt_data(PASSWORD, encrypted_data)
```

### 加密公式（逆運算）

```python
def encrypt_data(key_bytes, data):
    """
    LCG 加密算法（解密的逆運算）
    """
    length = len(data)
    encrypted = bytearray(length)
    l_key = struct.unpack('<I', key_bytes)[0]
    
    for i in range(length):
        l_key = (l_key * 0x3D09) & 0xFFFFFFFF
        ltmp_key = l_key >> 0x10
        encrypted[i] = (data[i] + (ltmp_key & 0xFF)) & 0xFF
    
    return bytes(encrypted)
```

---

## Datas.sai 索引檔格式

### 整體結構

```
┌──────────────────────────────────────────────┐
│              Header (16 bytes)               │
├──────────────────────────────────────────────┤
│    Encrypted Entry Table (N × 16 bytes)      │
├──────────────────────────────────────────────┤
│    Encrypted Filename Table (variable)       │
└──────────────────────────────────────────────┘
```

### Header 結構（明文）

| 偏移 | 大小 | 類型 | 說明 |
|------|------|------|------|
| 0x00 | 4 | bytes | 未知（可能是版本或魔數） |
| 0x04 | 4 | uint32 LE | **entries_count** — 檔案數量 |
| 0x08 | 4 | uint32 LE | **filename_size** — 檔名表總大小 (bytes) |
| 0x0C | 4 | bytes | 未知（保留） |

### Entry Table（解密後）

每個條目 16 bytes：

| 偏移 | 大小 | 類型 | 說明 |
|------|------|------|------|
| 0x0C | 4 | uint32 LE | **name_offset** — 檔名在 Filename Table 中的偏移 |

---

## 大陸(及新版)變體格式

部分版本（如大陸熱舞 Online）使用擴展的 24 bytes 結構。

### Header 結構 (24 bytes)

| 偏移 | 大小 | 類型 | 說明 |
|------|------|------|------|
| 0x00 | 4 | bytes | `SDO\0` 魔數 |
| 0x04 | 4 | uint32 LE | **entries_count** — 檔案數量 |
| 0x08 | 4 | uint32 LE | **filename_size** — 檔名表總大小 |
| 0x0C | 12 | bytes | 未知 (通常為 0) |

### Entry Table (24 bytes)

| 偏移 | 大小 | 類型 | 說明 |
|------|------|------|------|
| 0x00 | 4 | uint32 LE | file_id |
| 0x04 | 4 | uint32 LE | size |
| 0x08 | 4 | uint32 LE | offset |
| 0x0C | 4 | uint32 LE | name_offset |
| 0x10 | 4 | uint32 LE | 未知 |
| 0x14 | 4 | uint32 LE | **sac_id** — 對應的分卷編號 (`Datas_p{sac_id}.sac`) |

- 如果 `sac_id` 不為 0，則資料存儲在 `Data/Datas_p{sac_id}.sac`。
- 如果 `sac_id` 為 0，通常對應主資料包 `Datas.sac`。


### Filename Table（解密後）

- 連續的 NULL 結尾字串
- 編碼：GBK / GB18030
- 使用 `\` 作為路徑分隔符
- 每個 Entry 的 `name_offset` 指向此表中的起始位置

---

## Datas.sac 資料檔格式

資料檔為純粹的二進位連接，無額外結構：

```
┌─────────────────────────────────┐
│         File 0 Data             │
├─────────────────────────────────┤
│         File 1 Data             │
├─────────────────────────────────┤
│            ...                  │
├─────────────────────────────────┤
│         File N-1 Data           │
└─────────────────────────────────┘
```

- 各檔案依序排列，無填充
- 使用 Entry Table 中的 `offset` 和 `size` 定位讀取
- 資料為**明文**（不加密）

---

## 完整解壓流程

```python
import struct
import os

def extract_datas(sai_path, sac_path, output_dir):
    """
    完整解壓 Datas.sai + Datas.sac
    """
    PASSWORD = b'\x61\xf9\x53\x7c'
    
    # 1. 讀取索引檔
    with open(sai_path, 'rb') as f:
        f.read(4)  # 跳過未知 4 bytes
        entries_count = struct.unpack('<I', f.read(4))[0]
        filename_size = struct.unpack('<I', f.read(4))[0]
        f.read(4)  # 跳過未知 4 bytes
        
        encrypted_entries = f.read(entries_count * 16)
        encrypted_filenames = f.read(filename_size)
    
    # 2. 解密索引表和檔名表
    entries_raw = decrypt_data(PASSWORD, encrypted_entries)
    filenames_raw = decrypt_data(PASSWORD, encrypted_filenames)
    
    # 3. 解析並提取檔案
    with open(sac_path, 'rb') as sac_f:
        for i in range(entries_count):
            off = i * 16
            file_id, size, offset, name_off = struct.unpack(
                '<IIII', entries_raw[off:off+16]
            )
            
            # 取得檔名（NULL 結尾）
            name_end = filenames_raw.find(b'\x00', name_off)
            if name_end != -1:
                rel_path = filenames_raw[name_off:name_end].decode('gbk', 'ignore')
            else:
                rel_path = filenames_raw[name_off:].decode('gbk', 'ignore')
            
            if not rel_path:
                continue
            
            # 清理 Windows 非法字元
            invalid_chars = '<>:"|?*'
            clean_path = rel_path.replace('\\', os.sep)
            for c in invalid_chars:
                clean_path = clean_path.replace(c, '_')
            
            full_path = os.path.join(output_dir, clean_path)
            os.makedirs(os.path.dirname(full_path), exist_ok=True)
            
            # 讀取並寫入檔案
            sac_f.seek(offset)
            data = sac_f.read(size)
            
            with open(full_path, 'wb') as out_f:
                out_f.write(data)
    
    print(f"提取完成: {entries_count} 個檔案")
```

---

## 完整加壓流程

```python
import struct
import os

def pack_datas(input_dir, sai_path, sac_path):
    """
    將目錄打包為 Datas.sai + Datas.sac
    """
    PASSWORD = b'\x61\xf9\x53\x7c'
    
    # 1. 收集所有檔案
    file_list = []
    for root, dirs, files in os.walk(input_dir):
        for fname in files:
            full_path = os.path.join(root, fname)
            rel_path = os.path.relpath(full_path, input_dir)
            # 轉換為遊戲路徑格式（使用反斜線）
            game_path = rel_path.replace(os.sep, '\\')
            file_list.append((full_path, game_path))
    
    # 2. 建立檔名表
    filename_table = bytearray()
    name_offsets = []
    for _, game_path in file_list:
        name_offsets.append(len(filename_table))
        filename_table.extend(game_path.encode('gbk') + b'\x00')
    
    # 3. 寫入 SAC 並記錄偏移
    entries = []
    with open(sac_path, 'wb') as sac_f:
        for i, (full_path, _) in enumerate(file_list):
            offset = sac_f.tell()
            
            with open(full_path, 'rb') as f:
                data = f.read()
            
            sac_f.write(data)
            
            entries.append({
                'file_id': i,
                'size': len(data),
                'offset': offset,
                'name_offset': name_offsets[i]
            })
    
    # 4. 建立 Entry Table
    entry_table = bytearray()
    for e in entries:
        entry_table.extend(struct.pack('<IIII',
            e['file_id'],
            e['size'],
            e['offset'],
            e['name_offset']
        ))
    
    # 5. 加密並寫入 SAI
    encrypted_entries = encrypt_data(PASSWORD, bytes(entry_table))
    encrypted_filenames = encrypt_data(PASSWORD, bytes(filename_table))
    
    with open(sai_path, 'wb') as sai_f:
        sai_f.write(b'\x00\x00\x00\x00')  # 未知 4 bytes
        sai_f.write(struct.pack('<I', len(file_list)))
        sai_f.write(struct.pack('<I', len(filename_table)))
        sai_f.write(b'\x00\x00\x00\x00')  # 未知 4 bytes
        sai_f.write(encrypted_entries)
        sai_f.write(encrypted_filenames)
    
    print(f"打包完成: {len(file_list)} 個檔案")
```

---

## 注意事項

1. **編碼問題**：檔名使用 GBK 編碼，處理中文檔名時需注意
2. **路徑分隔**：遊戲內部使用 `\`，提取時需轉換為系統路徑分隔符
3. **非法字元**：Windows 不允許 `<>:"|?*` 等字元，提取時需替換
4. **大型檔案**：部分 SAC 檔案可能很大，建議使用串流方式處理

---

## 相關工具

- `tools/extract_datas.py` — 解壓工具，從 .sai/.sac 提取所有檔案
