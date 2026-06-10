# MOT / HRC 骨骼動畫檔格式說明

本文件說明 **熱舞 Online / Super Dancer Online** 系列遊戲的角色骨骼與動作檔格式：

- **`.MOT`** — Motion，骨骼動畫（每骨頭一組關鍵影格：rotation / scale / position）
- **`.HRC`** — Hierarchy，骨架（bind pose + 骨頭名稱，供 MOT 配對）

這兩種檔案都是**純二進位、無壓縮、無加密**，用 little-endian 存放。

> 解包流程參考 [`DATAS_SAI_SAC_FORMAT.md`](./DATAS_SAI_SAC_FORMAT.md)，解壓後可於 `sdox_offline/Extracted/MOTION/`、`sdox_offline/Extracted/AVATAR/` 取得。

---

## 檔案總覽

| 副檔名 | 典型路徑 | 用途 |
|--------|----------|------|
| `.MOT` | `MOTION/*.MOT`、`AUMOTION/*.MOT`、`3DEFT/.../*.MOT` | 單一動作（走路、跳舞、動作特效） |
| `.HRC` | `AVATAR/FEMALE.HRC`、`AVATAR/DOG.HRC` ... | 骨架（bind-pose） |

MOT 與 HRC **骨頭數必須相同**，且 MOT `bone_id` 直接對應 HRC 陣列索引（0-based）。

常見 HRC：

| 檔案 | 骨頭數 | 備註 |
|------|--------|------|
| `AVATAR/FEMALE.HRC`、`MALE.HRC` | 55 | 人物角色（Biped） |
| `AVATAR/WOMANBONE.HRC`、`MANBONE.HRC` | 54 | 無馬尾版 |
| `AVATAR/DOG.HRC` | 26 | 寵物狗 |
| `AVATAR/FOX0001.HRC`、`000001_FOX.HRC` | 62 / 57 | 寵物狐 |

---

## MOT 檔格式

### 結構

```
Header (16 bytes)
  char[N]   magic = "Animation" + 版本號字串
            (常見 "Animation0017" 或 "Animation001")
            剩餘 bytes 補 0 直到 16

Node × bone_count  (每個骨頭一組動畫資料)
  u32  bone_id            對應 HRC 陣列索引（0-based）
  u32  flag               恆為 0（同時作為 node 結束哨兵）
  u32  rot_key_count      旋轉關鍵影格數 (N_rot)
  u32  scale_key_count    縮放關鍵影格數 (N_scale)
  u32  pos_key_count      位移關鍵影格數 (N_pos)

  rot_keys   × N_rot      每筆 20 bytes：quat(x,y,z,w) + time
  scale_keys × N_scale    每筆 16 bytes：xyz + time
  pos_keys   × N_pos      每筆 16 bytes：xyz + time

Footer (4 bytes)
  float32   max_time      動畫最後一幀的 time，= 所有 key 的最大 time
```

> **MOT 檔沒有「bone_count」欄位**。reader 遇到「`flag != 0`」或「任一 count == 0」即停止解析 node 區段，
> 剩下的 4 bytes 就是 footer 的 `max_time`。寫檔時每個 node 的三個 key array 至少要有 1 筆，才能確保 reader
> 正確走完所有 node；官方檔未動的軌道都會寫「1 個 key，time = 0」作 padding。

### 欄位細節

| 欄位 | 型別 | 說明 |
|------|------|------|
| **magic** | `char[]` | 前 9 bytes 固定為 `"Animation"`，後續為版本字串。版本不影響解析邏輯。 |
| **bone_id** | `u32 LE` | **重要：** 是 HRC 骨頭陣列的索引（0..bone_count-1），**不是** HRC 裡儲存的 `id` 欄位（尾骨那欄都為 0 不可靠）。 |
| **flag** | `u32 LE` | 觀察上恆為 0，可視為結構對齊。 |
| **rot_key** | `float[5]` | `quat_x, quat_y, quat_z, quat_w, time`（單位四元數）。 |
| **scale_key** | `float[4]` | `sx, sy, sz, time`。 |
| **pos_key** | `float[4]` | `x, y, z, time`。 |
| **time** | `float` | 整數影格索引（0.0, 1.0, 2.0, ...）。**不是秒數**。 |

### 幀數 / 播放速度

- 動畫最後一幀的 time **直接寫在檔尾 `max_time`**（float32），無需自己掃 key 取最大值，但掃出來的結果與檔尾一致可以當作驗證。
- 動畫總幀數 = `max_time + 1`（time 為 0-based 整數幀）。
- 播放速度官方未硬編碼，一般做 **30 fps** 即可；音舞遊戲可依音樂節拍另外對時。
- 三種 key 的 `time` 彼此獨立，各自插值即可（quaternion 建議 **SLERP**，位移/縮放用 **LERP**）。

### 靜態 vs 動態骨頭

| 關鍵影格數 | 意義 |
|------------|------|
| `count == 1` | 該項整段動畫固定（靜態），唯一的 key 的 `time` 通常為 0。 |
| `count > 1`  | 該項有動畫，`time` 由 0 開始遞增。 |

典型情況：
- 大多數骨頭只動 **rotation**，`scale_count = pos_count = 1`（保持初始值）。
- 根骨頭 `Bip01` 可能有 **position** 動畫（locomotion 位移）。
- `Bip01_Pelvis`, `Bip01_Spine` 等有時有少量 position 變化（臀部搖動）。
- `scale` 絕大多數恆為 `(1, 1, 1)`。

> **⚠ `Bip01_Spine` 的 rotation 是遊戲引擎的陷阱**：官方 MOT 檔確實會寫入 `Bip01_Spine` 的旋轉資料（最高可達 30 度以上），
> 但實機反編譯顯示 `sdo_stand_alone.exe` 並沒有針對此骨頭做正常的逐骨 FK，而是將它的旋轉當成整個角色的剛體旋轉。
> 詳見下方[「遊戲引擎特殊行為」](#遊戲引擎特殊行為-bip01_spine)一節。

### 解析虛擬碼

```python
import struct, numpy as np

def read_mot(path):
    with open(path, 'rb') as f:
        data = f.read()
    assert data[:9] == b'Animation'
    off = 16
    nodes = []
    while off + 20 <= len(data):
        bone_id, flag, rc, sc, pc = struct.unpack('<5I', data[off:off+20])
        if flag != 0 or rc == 0 or sc == 0 or pc == 0:
            break
        rot = np.frombuffer(data, np.float32, rc*5, off+20).reshape(-1, 5)
        sc_base = off + 20 + rc*20
        scl = np.frombuffer(data, np.float32, sc*4, sc_base).reshape(-1, 4)
        pos_base = sc_base + sc*16
        pos = np.frombuffer(data, np.float32, pc*4, pos_base).reshape(-1, 4)
        nodes.append({'bone_id': bone_id, 'rot': rot, 'scale': scl, 'pos': pos})
        off += 20 + rc*20 + sc*16 + pc*16
    # footer: 4-byte float32 max_time（= 最後一幀）
    max_time = struct.unpack('<f', data[off:off+4])[0] if off + 4 <= len(data) else 0.0
    return nodes, max_time
```

### 範例

以 `MOTION/WWALK0001.MOT`（女生走路、55 骨、33 幀）為例：

| Node | bone_id | rot_cnt | scale_cnt | pos_cnt | 對應骨頭 | 備註 |
|------|---------|---------|-----------|---------|----------|------|
| 0 | 0 | 33 | 1 | 33 | Bip01 | 根骨頭，位移為世界座標 |
| 1 | 1 | 28 | 1 | 1  | Bip01_Pelvis | 旋轉 key 數少於總幀 → 後段停用時用最後一鍵沿用 |
| 2 | 2 | 33 | 1 | 32 | Bip01_Spine | 帶少量位移（⚠ 見 Spine 警告） |
| 3 | 3 | 33 | 1 | 33 | Bip01_R_Thigh | |
| ... | | | | | |

> 各軌 key 數不一定等於總幀數。動畫播放時 `time` 介於兩個 key 之間 → 插值；早於第一個 key → 取第一個；晚於最後一個 key → 取最後一個。

---

## HRC 檔格式

### 結構

```
Header (16 bytes)
  char[12]  magic = "Hierachy0020"   (注意拼字：Hierachy 少一個 r)
  u32       bone_count               骨頭數

Bone × bone_count  (每骨 112 bytes，緊接 header 後開始)
  float[4][4]   rest_matrix   bind-pose 本地矩陣（64 bytes, row-major / row-vector）
  u32           bone_id       HRC 儲存 id（非必要，尾骨會是 0）
  u32           subtree_next  若此骨開啟一條子鏈，則此欄為「該子鏈結束後下一個骨頭的陣列索引」；
                              否則為 0。用來 DFS 時快速跳過整條子鏈。
  char[12]      reserved      恆為 "Reserved    "
  char[28]      name          骨頭名稱，null terminated
```

檔案大小 = `16 + bone_count × 112`。

### 欄位細節

| 欄位 | 型別 | 說明 |
|------|------|------|
| **magic** | `char[12]` | `"Hierachy0020"`（`Hierachy` 是遊戲原拼法，少一個 `r`）。 |
| **bone_count** | `u32 LE` | 骨頭總數。 |
| **rest_matrix** | `float[4][4]` | **row-major / row-vector 慣例**：最後一列 `mat[3][0..2]` 是平移量（相對於父骨頭）。要在數學式 `v' = M @ v`（column-vector）下使用時，載入後需 `M = raw.T`。 |
| **bone_id** | `u32 LE` | 骨頭本身的 ID。**尾端骨頭（Toe0、Finger02、Ponytail22）這欄儲存為 0**，不能做為唯一識別。 |
| **subtree_next** | `u32 LE` | 只有「開啟一條新子鏈的骨頭」才有非零值，寫的是該子鏈結束後下一個骨頭的陣列索引。例如 `FEMALE.HRC`：`R_Thigh(3) → 7`、`L_Thigh(7) → 11`、`R_Clavicle(13) → 32`、`L_Clavicle(32) → 51`。**不能用這欄反推完整父子關係**（絕大多數骨頭為 0），父子請用命名規則推導。 |
| **reserved** | `char[12]` | 字面字串 `"Reserved    "`（含 4 個空白）。 |
| **name** | `char[28]` | 骨頭名稱字串，不足 28 bytes 補 `\0`。使用 3ds Max **Biped** 命名慣例。 |

### Biped 骨頭命名與父子關係

遊戲角色骨架為標準 3ds Max Biped，可用**命名規則直接推導父子**：

```
Bip01                       (root, 世界座標原點)
├── Bip01_Pelvis
│   ├── Bip01_L_Thigh → L_Calf → L_Foot → L_Toe0
│   ├── Bip01_R_Thigh → R_Calf → R_Foot → R_Toe0
│   └── Bip01_Spine → Spine1 → (Spine2 → Spine3)
│       ├── Bip01_Neck → Head → Ponytail2 → Ponytail21 → Ponytail22
│       ├── Bip01_L_Clavicle → L_UpperArm → L_Forearm → L_Hand
│       │                                                   └── Finger0..4, Finger01..41, Finger02..42
│       └── Bip01_R_Clavicle → R_UpperArm → R_Forearm → R_Hand → (右手手指)
```

> 手指命名規則：`FingerN`（根節）→ `FingerN1`（中節）→ `FingerN2`（末節），其中 `N ∈ {0,1,2,3,4}` 對應拇指到小指。

### 座標系

- **Biped 世界座標：Y 軸為高度**（向上）。
- 角色 root `Bip01` 的 rest_matrix 平移量例如 `(0, 32.62, 0)`，即站立時骨盆離地 32.62 單位。
- 繪圖時若使用 matplotlib / 3D 預設 Z 軸朝上，需做軸交換：`(X, Y_world, Z_world)` → `(X, Z_world, Y_world)`。

### 解析虛擬碼

```python
def read_hrc(path):
    with open(path, 'rb') as f:
        data = f.read()
    assert data[:8] == b'Hierachy'
    bone_count = struct.unpack('<I', data[12:16])[0]
    bones = []
    off = 16
    for i in range(bone_count):
        mat = np.frombuffer(data, np.float32, 16, off).reshape(4, 4)
        bid = struct.unpack('<I', data[off+64:off+68])[0]
        name = data[off+84:off+112].split(b'\x00', 1)[0].decode('ascii')
        bones.append({'idx': i, 'id': bid, 'name': name, 'mat': mat.copy()})
        off += 112
    return bones
```

---

## 動畫播放流程（Forward Kinematics）

完整播放一個 MOT 檔：

1. **載入 HRC**：取得骨頭名稱、陣列順序、bind-pose 矩陣（載入時建議先 `M = raw.T` 轉成 column-vector）。
2. **載入 MOT**：取得每骨頭的 rot/scale/pos 關鍵影格，以及檔尾的 `max_time`。
3. **建立父子表**：依 Biped 名稱建立每個骨頭的 parent index（HRC 的 `subtree_next` 欄不足以推導完整父子）。
4. **決定總幀數** `F = max_time + 1`。
5. **逐幀計算**：對每個時間 `t ∈ [0, F)`：
    - 對每個骨頭 `i`：
      - 若 MOT 中 `bone_id = i` 存在 → 插值取得 `rot`、`scale`、`pos`
      - 否則 → 沿用 HRC `rest_matrix`
      - 建立本地矩陣 `L = T(pos) × R(quat) × S(scale)`（其中 `R` 請見下方 quat 慣例說明）
      - 世界矩陣 `W[i] = W[parent[i]] × L`（root 的 parent 取單位矩陣）
    - 骨頭世界位置 = `W[i][0..2, 3]`（column-4 之 xyz）
6. **渲染**：以骨頭世界位置為端點，依命名規則連線畫 stick-figure，或進一步綁定 mesh 做 skinning。

### ⚠ MOT 四元數慣例（最容易踩的雷）

MOT 儲存的 `(qx, qy, qz, qw)` **是「row-vector 慣例」的四元數**（即 `v' = v · M`，數學上等價於標準 quat 的 **共軛**）。
若直接套用下方**標準 column-vector 公式** `v' = M · v` 建 `R`，FK 算出來的結果會是**反向旋轉**：

```
# 標準 column-vector 公式（v' = M · v 時的 R）
m00 = 1 - 2(y² + z²)     m01 = 2(xy - wz)      m02 = 2(xz + wy)
m10 = 2(xy + wz)         m11 = 1 - 2(x² + z²)  m12 = 2(yz - wx)
m20 = 2(xz - wy)         m21 = 2(yz + wx)      m22 = 1 - 2(x² + y²)
```

要在 column-vector FK（`W = W_parent @ L`）裡正確使用 MOT 的 quat，**兩種等價做法**擇一：

- **做法 A（推薦）**：套用上列標準公式後再 `R = R.T`。
- **做法 B**：建矩陣前先取共軛 `q' = (-qx, -qy, -qz, qw)`，再套標準公式。

`tools/mot_player.py` 用的是做法 A（見 `compose_local`）；若你 FK 結果出現「左右/前後顛倒」「整體往錯邊倒」的現象，先檢查這裡。

### 關鍵影格插值

- **Quaternion**：SLERP（若 `dot(q1,q2) < 0` 先翻號保證短路徑）
- **Position / Scale**：LERP

---

## 遊戲引擎特殊行為 (Bip01_Spine)

> 這一節是**實作 MOT 產生器（例如 VMD→MOT、BVH→MOT）時一定要知道的隱藏規則**。
> 純用 `mot_player.py` 做預覽看不出來，必須進到 `sdo_stand_alone.exe` 實機才會發現。

### 現象

對 `Bip01_Spine` (HRC idx=2) 寫入任何 rotation（不論大小、軸向），在遊戲內都會變成**整個角色像一根棍子剛體翻轉**（含雙腳連帶浮起），而不是正常的「脊椎彎腰」。
`mot_player.py` 的預覽沒問題——它會正確地只讓上半身彎下去——所以這不是 MOT 資料錯，是遊戲引擎的特殊處理。

其他脊椎/頭部骨頭都正常：`Bip01_Pelvis`、`Bip01_Spine1`、`Bip01_Neck`、`Bip01_Head` 都能被當成普通關節做局部旋轉。

### 原因（反編譯證據）

在 `sdo_stand_alone.exe` 的字串表中：

| 字串 | 是否出現 |
|------|----------|
| `"Bip01"` | 有 |
| `"Bip01_Pelvis"` | 有 |
| `"Bip01_Spine1"` | 有 |
| **`"Bip01_Spine"`** | **沒有** |

引擎只對 `Bip01`、`Bip01_Pelvis`、`Bip01_Spine1` 三個骨頭名稱做特殊尋找 / 掛鉤；`Bip01_Spine` 沒有對應的 code path，推測被當成「角色整體姿態輔助」般處理，旋轉套到了整個骨架的 root 而不是 `Spine` 節點本身。

### 對工具的影響

寫 VMD→MOT / BVH→MOT 的轉換工具時，**不要把「上半身/軀幹下段」的旋轉寫到 `Bip01_Spine`**。
解法是把原本要給 `Bip01_Spine` 的旋轉**完全省略**（讓它保持 rest），然後把該旋轉**累積到 `Bip01_Spine1`**。

在 MMD 骨架下這一步是免費的：MMD 的 `上半身2` 是 `上半身` 的子骨，其 world-rotation 天生就包含 `上半身` 的旋轉；
只要把 `上半身2 → Bip01_Spine1`（忽略 `上半身 → Bip01_Spine`），彎腰動作的完整角度就直接由 `Bip01_Spine1` 承擔，不會遺失任何資訊。

`tools/vmd_to_mot_blender.py` 裡的 `AVOID_BIP01_SPINE` 開關即是此行為（預設開啟）。

### 快速診斷指令

若懷疑某 MOT 在遊戲中變成棍子翻轉：

```powershell
# mot_player 正常、遊戲變棍子 → 幾乎可確定是踩到 Bip01_Spine
python tools/mot_player.py your.mot --hrc sdox_offline/Extracted/AVATAR/FEMALE.HRC
```

然後用下列方法之一檢查：
- 開檔看有沒有 `bone_id = 2` 的 node，`rot_cnt > 1` 且 quat 非單位四元數。
- 直接在轉換工具裡打開 `AVOID_BIP01_SPINE` 重算，若遊戲恢復正常即為此問題。

---

## 快速驗證

對每個 MOT：`parsed_bytes / file_size` 應為 100%，不然代表 bone 數或欄位對位錯誤。  
對每個 HRC：`16 + bone_count × 112 == file_size` 須成立。

播放器參考實作：[`tools/mot_player.py`](../tools/mot_player.py)

```powershell
# 即時播放（需 matplotlib）
python tools/mot_player.py sdox_offline/Extracted/MOTION/WWALK0001.MOT

# 輸出 GIF
python tools/mot_player.py WWALK0001.MOT --save wwalk.gif --fps 30

# 指定 HRC
python tools/mot_player.py MDANCE0002.MOT --hrc AVATAR/FEMALE.HRC
```

---

## 已知變體 / 注意事項

1. **magic 字串多版本**：`"Animation0017"`、`"Animation001"` + 其它非文字 byte 都出現過。前 9 bytes `"Animation"` 是唯一穩定的辨識特徵。
2. **HRC 尾骨 `bone_id = 0`**：`*_Toe0`、`*_Finger02`、`*_Finger12`、`*_Finger22`、`*_Finger32`、`*_Finger42`、`*_Ponytail22` 等末端骨頭皆如此。**不要拿 HRC 的 bone_id 欄位做索引**，一律用陣列位置。
3. **MOT bone 順序 ≠ HRC 順序**：官方 MOT 普遍採用「**leaf 先、root 最後**」的 canonical 順序（例如手指尖→手掌→手腕→...→Clavicle→Spine1→...→Bip01_Spine→Bip01_Pelvis→Bip01），HRC 反之為 root-first。實機測試顯示**遊戲引擎讀檔時是按 `bone_id` 查找，並不依賴 Node 出現順序**，所以自製工具可自由選擇輸出順序，但建議仍沿用官方 canonical 順序以免未來版本變動。
4. **3DEFT / 場景物件的 `.mot`**：部分場景物件（`ball.mot`、`zhuanpan.mot` ...）也使用相同 MOT 格式，但配對的 HRC 是 `3DEFT/XMESH/*.HRC`，骨頭數通常較少（1~10 個）。
5. **scale 實務上恆為 1**：大多數遊戲動作沒用到 non-uniform scale，實作時可直接忽略 scale 節省計算量。
6. **所有軌道至少要有 1 個 key**：MOT reader 用「`flag!=0` 或任一 count==0」作 node 結束哨兵，若寫出 `rot_cnt=0` 的空軌會讓後面的 node 全部被吃掉。靜態軌道請寫 1 筆 `time=0` 的 key。
7. **`Bip01_Spine` 旋轉在遊戲內會變成剛體旋轉**：詳見[「遊戲引擎特殊行為 (Bip01_Spine)」](#遊戲引擎特殊行為-bip01_spine)一節。自製動作時應避開寫入此骨的旋轉，改由 `Bip01_Spine1` 承擔。
