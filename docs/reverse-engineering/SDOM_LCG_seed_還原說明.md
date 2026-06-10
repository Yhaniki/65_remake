# SDOM 內嵌 `.gn`：LCG seed 還原方法說明

本文說明 [`sdom_gn_chart_decrypt.py`](sdom_gn_chart_decrypt.py) 中 **`recover_seed()`** 如何從檔案內已知明文還原 LCG 初始 seed。演算法與 DDRM 本文加密相同（乘數 `0x3D09`），細節可對照 [`GN_加解密說明.md`](GN_加解密說明.md)。

---

## 1. 為什麼可以還原 seed？

SDOM 內嵌檔在內嵌 StepFile 之後的資料區，結構為：

| 區段 | 長度 | 說明 |
|------|------|------|
| `inner[0:300]` | 300 | **明文** StepFile 表頭（與解密後完全一致） |
| `inner[300:]` | 變長 | **密文** = 對「整段 StepFile body」（表頭 300 + 譜面）做 LCG 串流加密後的結果 |

因此對任意位元組索引 ![i 範圍](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Di%20%5Cin%20%5B0%2C%20299%5D)：

<p align="center"><img src="https://latex.codecogs.com/png.image?%5Cdpi%7B110%7D%5Ctexttt%7Bks%7D%5Bi%5D%20%3D%20%28%5Ctexttt%7Bencrypted%7D%5Bi%5D%20-%20%5Ctexttt%7Bheader%7D%5Bi%5D%29%20%5Cbmod%20256" alt="keystream 定義"/></p>

前 300 個 keystream byte **完全已知**，無需猜測譜面內容。這是典型的**已知明文攻擊**（known-plaintext）。

---

## 2. LCG 與加／解密（與程式一致）

- 乘數：`M = 0x3D09`
- 狀態：`uint32`，每次處理一個明文 byte 前先更新狀態
- 解密時（與 `decrypt_body()` 相同）：

```text
state ← seed
重複 len(encrypted) 次:
    state ← (state * M) mod 2^32
    k     ← (state >> 16) & 0xFF
    plain[i] ← (cipher[i] - k) mod 256
```

因此：

- 第一個 keystream byte **不是**用「初始 seed」直接算出的高位，而是用
  **第一次乘法之後**的狀態：

<p align="center"><img src="https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1%20%3D%20%5Cmathrm%7Bseed%7D%20%5Ccdot%20M%20%5Cpmod%7B2%5E%7B32%7D%7D%2C%5Cquad%20k_0%20%3D%20%28S_1%20%5Cgg%2016%29%20%5Cbmod%20256" alt="S1 與 k0"/></p>

- 之後：

<p align="center"><img src="https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_%7Bi%2B1%7D%20%3D%20S_i%20%5Ccdot%20M%20%5Cpmod%7B2%5E%7B32%7D%7D%2C%5Cquad%20k_i%20%3D%20%28S_%7Bi%2B1%7D%20%5Cgg%2016%29%20%5Cbmod%20256" alt="狀態遞推"/></p>

（程式裡把「第一次乘法後的狀態」記作還原迴圈中的 `state`，即上式的 ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1)。）

---

## 3. 還原目標：先找 ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1)，再算 seed

已知 ![k 序列](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Dk_0%2C%20k_1%2C%20%5Cldots%2C%20k_%7Bn-1%7D)（通常取 ![n=16](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Dn%20%3D%2016) 即可篩掉幾乎所有假解）。

### 3.1 ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1) 只有部分位元未知

由 ![k0 定義](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Dk_0%20%3D%20%28S_1%20%5Cgg%2016%29%20%5Cbmod%20256) 可知：**![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1) 的 bit 16–23 等於 ![k0](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Dk_0)**。

其餘位元中：

- bit 0–15：共 16 bit，未知 → **![2^16](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7D2%5E%7B16%7D)** 種
- bit 24–31：共 8 bit，未知 → **![2^8](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7D2%5E%7B8%7D)** 種

合計 **![2^24](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7D2%5E%7B24%7D%20%3D%2016%7B%2C%7D777%7B%2C%7D216)** 種候選，在一般 PC 上約數秒可掃完。

對每個候選 ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1)：

1. 算 ![S2](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_2%20%3D%20S_1%20%5Ccdot%20M)，檢查 ![檢查 k1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7D%28S_2%20%5Cgg%2016%29%20%5Cbmod%20256%20%5Cstackrel%7B%3F%7D%7B%3D%7D%20k_1)
2. 再算 ![S3](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_3%20%3D%20S_2%20%5Ccdot%20M)，檢查是否等於 ![k2](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Dk_2)
3. 再算 ![S4](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_4)，檢查是否等於 ![k3](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Dk_3)
   （前三步可快速剔除絕大多數候選）
4. 對 ![i 迴圈](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Di%20%3D%201%20%5Cldots%20n-1) 重複乘 ![M](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DM) 並比對 ![ki](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Dk_i)，全部通過則接受此 ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1)

### 3.2 從 ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1) 反推 seed

<p align="center"><img src="https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1%20%3D%20%5Cmathrm%7Bseed%7D%20%5Ccdot%20M%20%5Cpmod%7B2%5E%7B32%7D%7D%20%5CRightarrow%20%5Cmathrm%7Bseed%7D%20%3D%20S_1%20%5Ccdot%20M%5E%7B-1%7D%20%5Cpmod%7B2%5E%7B32%7D%7D" alt="seed 反推"/></p>

![M 值](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DM%20%3D%200x3D09) 為奇數，在模 ![2^32](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7D2%5E%7B32%7D) 下必有乘法反元素。程式使用：

```python
INV_MULT = pow(MULT, -1, 2**32)
seed = (state * INV_MULT) & 0xFFFFFFFF
```

其中 `state` 即為還原得到的 ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1)。

---

## 4. 為什麼會出現多個「等價 seed」？

實務上常得到 **256 個**彼此不同的 `seed`，但用它們各自跑 `decrypt_body()` 得到的明文**完全相同**。

原因是： keystream 只依賴 ![Si 高位鏈](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7D%28S_i%20%5Cgg%2016%29%20%5Cbmod%20256) 這條鏈；對 ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1) 而言，**僅 bit 16–23 會進入第一個 ![k0](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Dk_0)**，而乘上固定的 ![M](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DM) 後，**某些對 ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1) 高位元的改動**在多次「乘 ![M](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DM) 再取 bit 16–23」後仍產生相同的 byte 序列。因此存在一整族等價的初始 `seed`，任取其一即可解密。

程式目前取 `seeds[0]` 作為代表。

---

## 5. 與 `recover_seed()` 的對應關係

實作見 [`sdom_gn_chart_decrypt.py`](sdom_gn_chart_decrypt.py) 中：

- **`recover_seed(header, encrypted, check_bytes=16)`**
  - `ks[i] = (encrypted[i] - header[i]) & 0xFF`
  - 雙層迴圈：`hi in 0..255`（對應 ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1) 的 bit 24–31）、`lo in 0..65535`（對應 bit 0–15），組出

<p align="center"><img src="https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1%20%3D%202%5E%7B24%7D%20%5Ccdot%20%5Cmathrm%7Bhi%7D%20%2B%202%5E%7B16%7D%20%5Ccdot%20k_0%20%2B%20%5Cmathrm%7Blo%7D" alt="S1 位元組合（與 hi、lo 位移後 OR 等價）"/></p>

  - 用 ![k1,k2,k3](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7Dk_1%2C%20k_2%2C%20k_3) 快速過濾，再以 `check_bytes` 做完整比對
  - 通過後：`seed = (state * INV_MULT) & 0xFFFFFFFF`

- **`decrypt_body(seed, encrypted)`**
  與上節「解密時」敘述一致，用還原出的任一等價 `seed` 還原整段 body。

---

## 6. 驗證方式（工具內建）

還原並解密後，工具會做：

1. **表頭自洽**：解密出的前 300 byte 必須與檔案中明文 `inner[0:300]` 逐 byte 相同。
2. **結構檢查**（可選）：Easy 開頭 StepFrame、三難度 `type=9` 等啟發式（部分曲目可能不完全符合）。

若第 1 步失敗，代表檔案不是此格式或演算法已變更。

---

## 7. 複雜度與實測量級

| 項目 | 數量級 |
|------|--------|
| ![S1](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7DS_1) 候選數 | ![2^24 約](https://latex.codecogs.com/png.image?%5Cdpi%7B110%7D2%5E%7B24%7D%20%5Capprox%201.68%20%5Ctimes%2010%5E%7B7%7D) |
| 典型執行時間（單檔） | 約數秒（視 CPU） |
| `check_bytes` | 預設 16；增大可減少極罕見的假陽性，但略增時間 |

---

## 8. 與 DDRM `seed2` 的關係（選讀）

在帶 `ddrm` 包裝的 `.gn` 中，`+0x24` 處常存 **經 seed1 保護的欄位**；其中一層語意即「用於本文 LCG 的 seed2」。
SDOM 內嵌檔把 **同一套 LCG** 用在「整段 body」上，且檔內另附 **一份明文表頭**；因此**不必**先有 `ddrm` 也能用本節方法還原等價 seed 並解密。

若手邊同時有對應的 `de_sdom*.gn`，可把其 DDRM 標頭中 **加密態的 `+0x24` dword** 與本工具還原的 seed 對照，通常會落在同一組等價族中（僅高位可能不同）。

---

*若與實檔或新版客戶端行為不一致，以實際二進位與 `sdom_gn_chart_decrypt.py` 為準。*
