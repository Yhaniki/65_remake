# MMD VMD 舞蹈動作格式說明

## 1. VMD 是什麼

VMD，全名為 **Vocaloid Motion Data**，是 MikuMikuDance（MMD）用來儲存動畫動作的二進位檔案格式。

在舞蹈動作中，VMD 主要記錄：

- 骨骼關鍵幀，例如手、腳、身體、頭部的位置與旋轉
- 表情 / Morph 關鍵幀，例如眨眼、張嘴、笑臉
- 相機關鍵幀
- 燈光關鍵幀
- 陰影關鍵幀
- IK 顯示與開關狀態

其中，真正定義「舞蹈動作」的核心是 **Bone Keyframe**，也就是每個骨骼在指定影格的位移、旋轉與補間曲線。

---

## 2. 整體檔案結構

VMD 是二進位格式，通常使用 Shift-JIS 編碼儲存日文名稱。

```text
VMD File
├── Header
│   ├── Signature
│   └── Model Name
│
├── Bone Keyframes
│   ├── Bone Frame Count
│   └── Bone Frame[]
│
├── Morph Keyframes
│   ├── Morph Frame Count
│   └── Morph Frame[]
│
├── Camera Keyframes
│   ├── Camera Frame Count
│   └── Camera Frame[]
│
├── Light Keyframes
│   ├── Light Frame Count
│   └── Light Frame[]
│
├── Shadow Keyframes
│   ├── Shadow Frame Count
│   └── Shadow Frame[]
│
└── IK / Display Keyframes
    ├── IK Frame Count
    └── IK Frame[]
```

---

## 3. Header

### 3.1 Signature

固定長度 30 bytes。

常見內容：

```text
Vocaloid Motion Data file
Vocaloid Motion Data 0002
```

`Vocaloid Motion Data 0002` 是較新的 Multi-Model 版本。

### 3.2 Model Name

| 版本 | 長度 | 說明 |
|---|---:|---|
| 舊版 | 10 bytes | 對應模型名稱 |
| 新版 | 20 bytes | 對應模型名稱 |

如果模型名稱或骨骼名稱不一致，MMD 仍可能載入 VMD，但只有名稱對得上的骨骼或 morph 會被套用。

---

## 4. Bone Keyframe：舞蹈動作的核心

Bone Keyframe 用來描述某一個骨骼在某一個影格的姿態。

### 4.1 Bone Frame Count

```c
uint32_t boneFrameCount;
```

表示後面有多少筆骨骼關鍵幀。

### 4.2 Bone Frame 結構

```c
struct VmdBoneFrame
{
    char     boneName[15];
    uint32_t frameNumber;

    float    positionX;
    float    positionY;
    float    positionZ;

    float    rotationX;
    float    rotationY;
    float    rotationZ;
    float    rotationW;

    uint8_t  interpolation[64];
};
```

### 4.3 欄位說明

| 欄位 | 型別 | 說明 |
|---|---|---|
| `boneName[15]` | char[15] | 骨骼名稱，通常是日文名稱，Shift-JIS 編碼 |
| `frameNumber` | uint32 | 關鍵幀所在影格 |
| `positionX/Y/Z` | float | 骨骼位置偏移 |
| `rotationX/Y/Z/W` | float | 骨骼旋轉，使用 quaternion |
| `interpolation[64]` | uint8[64] | 補間曲線資料 |

---

## 5. 舞蹈動作如何被定義

MMD 的舞蹈不是逐格儲存每一幀，而是用「關鍵幀」定義。

例如：

```text
Frame 0:
  左腕 rotation = A

Frame 15:
  左腕 rotation = B

Frame 30:
  左腕 rotation = C
```

播放時，MMD 會根據 `frameNumber` 與 `interpolation` 自動計算中間影格。

也就是說：

```text
舞蹈動作 = 多個骨骼 + 多個時間點 + 每個時間點的位置 / 旋轉 + 補間曲線
```

---

## 6. 骨骼位置與旋轉

### 6.1 位置 Position

`positionX/Y/Z` 是相對於模型初始姿態的偏移量，不是世界座標的絕對位置。

例如：

```text
模型預設骨骼位置 = (1, 2, 3)
VMD position = (10, 25, 30)

實際結果約為：
(11, 27, 33)
```

### 6.2 旋轉 Rotation

VMD 使用 quaternion 儲存旋轉：

```text
rotationX
rotationY
rotationZ
rotationW
```

Quaternion 適合用來表示 3D 旋轉，可以避免 Euler angle 的萬向節鎖問題。

---

## 7. Interpolation 補間資料

`interpolation[64]` 決定兩個關鍵幀之間如何過渡。

它通常對應到以下幾個通道：

- X 位置
- Y 位置
- Z 位置
- 旋轉

每個通道可用 Bezier 曲線控制動作速度，例如：

- 線性移動
- 慢慢開始
- 快速收尾
- 先快後慢

簡化理解：

```text
沒有 interpolation：
  動作等速變化

有 interpolation：
  動作可以有加速、減速、停頓感
```

---

## 8. Morph Keyframe：表情與形變

Morph Keyframe 用來控制表情、嘴型、眼睛或其他 morph。

### 8.1 Morph Frame Count

```c
uint32_t morphFrameCount;
```

### 8.2 Morph Frame 結構

```c
struct VmdMorphFrame
{
    char     morphName[15];
    uint32_t frameNumber;
    float    weight;
};
```

### 8.3 欄位說明

| 欄位 | 型別 | 說明 |
|---|---|---|
| `morphName[15]` | char[15] | Morph 名稱 |
| `frameNumber` | uint32 | 關鍵幀影格 |
| `weight` | float | Morph 權重，通常 0.0 到 1.0 |

例如：

```text
Frame 0:   blink = 0.0
Frame 10:  blink = 1.0
Frame 15:  blink = 0.0
```

這就會形成一次眨眼。

---

## 9. Camera Keyframe

Camera Keyframe 用來定義鏡頭動畫。

```c
struct VmdCameraFrame
{
    uint32_t frameNumber;

    float    distance;
    float    targetX;
    float    targetY;
    float    targetZ;

    float    rotationX;
    float    rotationY;
    float    rotationZ;

    uint8_t  interpolation[24];

    uint32_t fov;
    uint8_t  perspective;
};
```

| 欄位 | 說明 |
|---|---|
| `distance` | 相機到目標點的距離 |
| `targetX/Y/Z` | 相機看向的目標位置 |
| `rotationX/Y/Z` | 相機旋轉 |
| `interpolation[24]` | 相機補間曲線 |
| `fov` | 視野角 |
| `perspective` | 是否使用透視 |

---

## 10. Light Keyframe

Light Keyframe 用來控制燈光顏色與方向。

```c
struct VmdLightFrame
{
    uint32_t frameNumber;

    float    red;
    float    green;
    float    blue;

    float    positionX;
    float    positionY;
    float    positionZ;
};
```

`red / green / blue` 通常是 0.0 到 1.0 的浮點數。

---

## 11. Shadow Keyframe

Shadow Keyframe 用來控制陰影模式與範圍。

```c
struct VmdShadowFrame
{
    uint32_t frameNumber;
    uint8_t  mode;
    float    distance;
};
```

| mode | 說明 |
|---:|---|
| 0 | 關閉陰影 |
| 1 | Shadow mode 1 |
| 2 | Shadow mode 2 |

---

## 12. IK / Display Keyframe

IK Frame 用來控制 IK 顯示與指定 IK 骨骼是否啟用。

```c
struct VmdIkFrame
{
    uint32_t frameNumber;
    uint8_t  display;
    uint32_t ikBoneCount;
    VmdIkBone ikBones[];
};

struct VmdIkBone
{
    char    boneName[20];
    uint8_t enabled;
};
```

| 欄位 | 說明 |
|---|---|
| `display` | 是否顯示 |
| `ikBoneCount` | 此影格包含多少個 IK 骨骼設定 |
| `boneName[20]` | IK 骨骼名稱 |
| `enabled` | IK 是否啟用 |

---

## 13. 一個簡化版 VMD 舞蹈資料模型

如果只想理解舞蹈動作，可以先忽略相機、燈光、陰影，只看骨骼與 morph。

```c
struct SimpleVmdMotion
{
    VmdHeader header;

    uint32_t boneFrameCount;
    VmdBoneFrame *boneFrames;

    uint32_t morphFrameCount;
    VmdMorphFrame *morphFrames;
};
```

概念上等同於：

```text
motion = {
    modelName: "初音ミク",
    bones: [
        {
            boneName: "左腕",
            frame: 0,
            position: [0, 0, 0],
            rotation: [x, y, z, w],
            interpolation: [...]
        },
        {
            boneName: "左腕",
            frame: 15,
            position: [0, 0, 0],
            rotation: [x, y, z, w],
            interpolation: [...]
        }
    ],
    morphs: [
        {
            morphName: "まばたき",
            frame: 10,
            weight: 1.0
        }
    ]
}
```

---

## 14. 實作 Parser 時的注意事項

### 14.1 Endianness

VMD 常見數值使用 little-endian。

例如：

```text
uint32
float32
```

讀取時要注意 byte order。

### 14.2 字串編碼

VMD 的骨骼、morph、模型名稱通常是 Shift-JIS。

讀取流程通常是：

```text
讀固定長度 bytes
移除結尾 null byte
用 Shift-JIS decode
```

### 14.3 名稱匹配很重要

VMD 不直接綁定骨骼 ID，而是用骨骼名稱匹配。

所以如果模型骨骼名稱和 VMD 裡的骨骼名稱不同，該骨骼動作就不會正確套用。

### 14.4 關鍵幀不一定排序

讀取後最好依照：

```text
boneName
frameNumber
```

排序，方便後續插值與播放。

---

## 15. 最小解析流程

```text
1. 讀取 30 bytes signature
2. 根據 signature 判斷 modelName 長度
3. 讀取 modelName
4. 讀取 boneFrameCount
5. 依序讀取 boneFrameCount 筆 BoneFrame
6. 讀取 morphFrameCount
7. 依序讀取 MorphFrame
8. 讀取 cameraFrameCount
9. 依序讀取 CameraFrame
10. 繼續讀取 light / shadow / IK 區段
```

---

## 16. 總結

MMD 的 VMD 舞蹈動作本質上是一組時間序列資料。

最重要的資料是：

```text
骨骼名稱
影格編號
位置
旋轉 quaternion
補間曲線
```

MMD 播放時會根據這些關鍵幀，對每個骨骼進行插值，最後形成連續的舞蹈動畫。
