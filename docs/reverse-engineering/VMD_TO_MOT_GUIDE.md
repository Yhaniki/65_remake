# VMD → MOT 轉換指南

把 MikuMikuDance (MMD) 的 **VMD** 動作檔轉成 **熱舞 Online / SDO** 的 **MOT** 動作檔，讓 MMD 舞蹈可以在遊戲裡播放。

主腳本：[`tools/vmd_to_mot_blender.py`](../tools/vmd_to_mot_blender.py)

相關格式文件：
- [`MOT_HRC_FORMAT.md`](./MOT_HRC_FORMAT.md) — SDO 動作/骨架格式
- [`mmd_vmd_format.md`](./mmd_vmd_format.md) — MMD VMD 格式

---

## 1. 原理

VMD 和 MOT 都是骨骼動畫，但骨架完全不同，沒辦法直接轉，需要走三層對應：

```
┌─────────────┐   bone map    ┌──────────────┐  axis convert  ┌─────────────┐
│  MMD (VMD)  │ ─────────────> │ Blender pose │ ──────────────>│ SDO Biped   │
│  日文骨名    │   + IK bake    │ 世界空間矩陣   │   + rest 套用  │  55 bone    │
└─────────────┘                └──────────────┘                 └─────────────┘
```

三個關鍵問題：

1. **骨頭對應**：MMD 約 100 多個日文骨頭 ↔ SDO Biped 55 個英文骨頭
2. **IK 解算**：VMD 的腳用 IK controller 控制（`左足ＩＫ`），SDO MOT 只吃純 FK
3. **座標系轉換**：MMD（左手、Y-up、+Z 前）↔ Blender（右手、Z-up、-Y 前）↔ SDO（右手、Y-up、-Z 前）

解法：藉 Blender + [mmd_tools](https://extensions.blender.org/add-ons/mmd-tools/) 處理 1、2；自己寫 retargeting + MOT writer 處理 3。

---

## 2. 前置準備

### 2.1 安裝 Blender + mmd_tools

1. 下載 [Blender 4.2 LTS 以上](https://www.blender.org/download/)（測過 5.1.1）
2. 安裝 mmd_tools：`Edit → Preferences → Get Extensions → 搜 "mmd_tools" → Install`

安裝後 mmd_tools 的 addon id 是 `bl_ext.user_default.mmd_tools`（腳本會自動啟用）。

### 2.2 準備檔案

| 檔案 | 說明 | 取得方式 |
|------|------|----------|
| `.vmd` | MMD 動作檔 | 舞蹈作者提供（例：`シニカルナイトプラン.vmd`） |
| `.pmx` | MMD 模型檔 | 任一相容這支 VMD 的模型（例：YYB Hatsune Miku 10th） |
| `.HRC` | SDO 骨架 | 從遊戲包解出，一般用 `AVATAR/FEMALE.HRC` |

> PMX 只是讓 mmd_tools 建出正確的骨架以解讀 VMD，**轉出來的 MOT 並不包含模型**，角色外觀仍由遊戲決定。

---

## 3. 使用方法

### 基本用法

```bash
blender --background --python tools/vmd_to_mot_blender.py -- \
    "path/to/dance.vmd" \
    "path/to/model.pmx" \
    "path/to/FEMALE.HRC" \
    -o "path/to/output.mot"
```

**注意 `--` 之後才是腳本參數**，`--` 前的參數是給 Blender 的。

### 實際範例（Windows PowerShell）

```powershell
& 'E:\SteamLibrary\steamapps\common\Blender\blender.exe' --background `
    --python 'g:\sdo_mot\tools\vmd_to_mot_blender.py' -- `
    'g:\sdo_mot\シニカルナイトプラン\シニカルナイトプラン.vmd' `
    'g:\sdo_mot\YYB Hatsune Miku_10th\YYB Hatsune Miku_10th_v1.02.pmx' `
    'g:\sdo_mot\sdox_offline\Extracted\AVATAR\FEMALE.HRC' `
    -o 'g:\sdo_mot\cynical_night_plan.mot'
```

耗時：長度 6000 幀的動作約 3 ∼ 5 分鐘（Bake IK + Retarget）。

### 參數說明

| 位置 | 說明 | 必填 |
|------|------|------|
| 1 | VMD 路徑 | ✓ |
| 2 | PMX 路徑 | ✓ |
| 3 | HRC 路徑 | ✓ |
| `-o` / `--output` | 輸出 MOT 路徑 | ✓ |

### 驗證結果

用專案內的播放器預覽：

```bash
python tools/mot_ui.py g:\sdo_mot\cynical_night_plan.mot
```

---

## 4. 轉換流程（腳本做了什麼）

1. **啟用 mmd_tools、清空場景**
2. **匯入 PMX**：建立帶 IK constraint 的骨架，保留日文骨名
3. **匯入 VMD**：把動畫資料套到骨架上
4. **Bake 動畫**：用 `visual_keying=True` 把 IK 結果烤成每骨頭的 FK 旋轉關鍵幀
5. **讀 HRC**：載入 SDO Biped 骨架的 bind pose
6. **逐幀 Retarget**：對每一幀計算 `world-space delta rotation` 並應用
7. **寫 MOT**：轉成 row-major quaternion + 關鍵幀陣列

### Retargeting 核心公式（世界空間 delta）

對每個有對應的骨頭：

```python
# Blender 中 MMD 骨頭的 rest / frame 世界旋轉
R_mmd_rest  = armature.matrix_world @ mmd_pb.bone.matrix_local
R_mmd_frame = armature.matrix_world @ mmd_pb.matrix

# MMD 世界空間的旋轉 delta（從 rest 轉到當前幀）
R_delta_bl = R_mmd_frame @ R_mmd_rest.T

# 轉到 SDO 座標系
R_delta_sdo = A @ R_delta_bl @ A.T

# 套到 Biped rest pose 上
R_biped_frame = R_delta_sdo @ R_biped_rest
```

無對應的 Biped 骨頭（如 `Bip01_PonytailXX`）就維持 rest pose。

### 座標轉換矩陣 A

```
sdo_x =  blender_x       左右維持（+X = 角色左）
sdo_y =  blender_z       上下維持（+Y/+Z = 上）
sdo_z =  blender_y       前後對應（Blender -Y 前 → SDO -Z 前）
```

```
        ⎡ 1  0  0 ⎤
    A = ⎢ 0  0  1 ⎥   （det = -1，是鏡射）
        ⎣ 0  1  0 ⎦
```

**為什麼 det=-1 是對的**：MMD 原生是左手座標系，SDO/Blender 是右手座標系，轉換時必須一次鏡射才能讓旋轉方向（手性）對齊。

---

## 5. 骨頭對應表

定義在 `tools/vmd_to_mot_blender.py` 的 `BONE_MAP` 字典。精簡版：

| MMD（日文） | SDO Biped |
|-------------|-----------|
| センター / グルーブ | `Bip01` |
| 下半身 | `Bip01_Pelvis` |
| 上半身 / 上半身2 | `Bip01_Spine` / `Bip01_Spine1` |
| 首 / 頭 | `Bip01_Neck` / `Bip01_Head` |
| 左肩 / 右肩 | `Bip01_L_Clavicle` / `Bip01_R_Clavicle` |
| 左腕 / 左ひじ / 左手首 | `Bip01_L_UpperArm` / `Forearm` / `Hand` |
| 左足 / 左ひざ / 左足首 | `Bip01_L_Thigh` / `Calf` / `Foot` |
| 左つま先 | `Bip01_L_Toe0` |

右邊 `右*` 對應 `Bip01_R_*`。MMD 的手指骨頭（左親指１ 等）也對應到 Biped `Finger0〜Finger4` 的對應節。

> **VMD 的 IK 骨頭（如 `左足ＩＫ`）不需要對應**，bake 之後已經變成 `左足 / 左ひざ / 左足首` 的 FK 旋轉。

---

## 6. 踩坑筆記（重要）

### 6.1 MOT 四元數是 row-major

雖然其他地方是 column-major，但 MOT 內的 quaternion 對應的 rotation matrix 是 row-major 約定。寫入時：

```python
# numpy 的 R 是 column-vector 慣例，轉成 row-major 要取共軛
q_row = quat_from_matrix(R.T)
```

### 6.2 MMD 角色在 Blender 中面朝 -Y

**這是最大的坑**。mmd_tools 匯入 PMX 時會做座標系轉換（MMD 左手 → Blender 右手），結果：

- MMD +Z (前方) → Blender **-Y** (前方) ← 不是 +Y！
- MMD +Y (上方) → Blender +Z (上方)
- MMD +X (角色左) → Blender +X (角色左)

可以用 mesh vertex 驗證：鼻尖在 Blender **-Y 側**，後腦在 **+Y 側**。

如果 A 矩陣假設「Blender +Y = MMD 前」，轉出來的所有動作都會**前後相反**（手前伸變後伸、腳掌朝後），但左右維持正確，非常難察覺。

### 6.3 IK bake 必須 visual_keying=True

```python
bpy.ops.nla.bake(
    frame_start=..., frame_end=..., step=1,
    only_selected=False, visual_keying=True,  # 關鍵！
    clear_constraints=True, use_current_action=True,
    bake_types={'POSE'},
)
```

沒這個 flag 的話 `pose_bone.matrix` 只讀到原始 keyframe 而不是 IK 解算後的結果，腳部會亂。

### 6.4 匯入 PMX 要保留日文骨名

```python
bpy.ops.mmd_tools.import_model(
    filepath=pmx_path,
    rename_bones=False,      # 保留日文
    fix_bone_order=False,
    clean_model=False,
    ...
)
```

不這樣做的話，bone 名會被轉成英文，BONE_MAP 就找不到對應。

### 6.5 VMD frame 1 可能已經不是 rest

很多 VMD 的第 1 幀已經有動作（不是 T-pose），所以 `bone.matrix_local`（Blender 建骨時算的 rest）才是真正的 rest 參考。腳本裡用的就是 `bone.matrix_local` 而非 frame 1 的姿勢。

---

## 7. 疑難排解

| 症狀 | 可能原因 | 解法 |
|------|----------|------|
| 整體動作前後相反 | A 矩陣前後方向寫錯 | 參考 6.2，確認 `A[2][1] = 1` 而非 `-1` |
| 左右相反 | A 矩陣 X 方向寫錯 | 確認 `A[0][0] = 1`，且 BONE_MAP 左右無誤 |
| 腳穿地板 / 飄空 | IK 沒 bake 成功 | 檢查 6.3 的 `visual_keying` |
| 手指沒動 | BONE_MAP 沒列到手指骨頭 | 擴充 BONE_MAP |
| 馬尾不會動 | 正常 | MMD 沒這骨頭，Biped ponytail 維持 rest |
| `ImportError: mmd_tools` | Extension 沒裝 | 參考 2.1 |
| `Not a valid PMX/VMD` | 檔案路徑含特殊字元 | 改短路徑或純 ASCII |
| 轉出的 MOT 角色是 T-pose | Bake 前沒先匯入 VMD 就跑 | 檢查 log `[2/6] 匯入 VMD` 是否成功 |

---

## 8. 進階：擴充新骨架 / 新模型

### 轉給不同 HRC

改參數即可（如 `MALE.HRC`）。前提是 HRC 用的也是 Biped 命名（`Bip01_*`），否則要改 `BONE_MAP`。

### 新增 BONE_MAP 項目

打開 `tools/vmd_to_mot_blender.py` 找 `BONE_MAP`，照既有格式加一行：

```python
BONE_MAP = {
    ...
    '新MMD骨名': 'Bip01_XXX',
}
```

存檔後重新執行即可。

### 調整輸出精度 / 幀率

目前輸出每一 Blender 幀 1 筆關鍵幀（通常 VMD 為 30 fps）。若要減量或補間，可在 `convert()` 的 frame loop 中加下取樣邏輯。

---

## 9. 已知限制

- **只支援 Biped 骨架類的 HRC**（FEMALE / MALE / WOMANBONE / MANBONE），動物骨架需另外寫 BONE_MAP
- **臉部表情（Morph）不轉**，VMD 的表情資料 MOT 本身也不支援
- **相機動畫（Camera VMD）不轉**
- **物理模擬**（裙子、頭髮飄動）不會跑，MMD 物理在 Blender 預設關閉
- Position 只從 `センター` / `グルーブ` 根骨頭取，其他骨頭只給 rotation
