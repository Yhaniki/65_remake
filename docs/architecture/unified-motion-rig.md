# 共用動作與模型:MMD ⇄ 熱舞(SDO)動作互通設計

> 狀態:**設計草案**(尚未實作)。目標是 MVP+ 之後動手時的藍圖。
> 相關:[dance-vmd.md](./dance-vmd.md)、[reverse-engineering/MOT_HRC_FORMAT.md](../reverse-engineering/MOT_HRC_FORMAT.md)、
> [reverse-engineering/mmd_vmd_format.md](../reverse-engineering/mmd_vmd_format.md)、
> [reverse-engineering/VMD_TO_MOT_GUIDE.md](../reverse-engineering/VMD_TO_MOT_GUIDE.md)

## 1. 目標

讓**動作格式**與**角色模型**完全解耦,任意組合都能播:

| 模型 \ 動作 | SDO MOT | MMD VMD |
|---|---|---|
| 熱舞角色(HRC Biped) | ✅ 現況 | ✅ 要做 |
| MMD 模型(PMX) | ✅ 要做 | ✅ 要做(最直接) |

**非目標**(本文件範圍外,另議):表情 morph 的完整對應、布料/物理、相機 VMD(見 [dance-vmd.md](./dance-vmd.md);可獨立先做)。

## 2. 核心觀念:正規骨架(canonical rig)當中樞

不要做 N×M 條兩兩轉換,而是定一個**中樞骨架**:所有**動作**轉成中樞姿勢、所有**模型**被中樞姿勢驅動。N 個來源 + M 個接收端,只需 N+M 個轉接器。

```
   MOT ─┐                        ┌─ SDO 角色 (HRC Biped)
        ├─►  canonical pose  ─►──┤
   VMD ─┘   (中樞骨架的每骨姿勢)    └─ MMD 模型 (PMX 骨架)
```

**選 canonical = SDO Biped(HRC,55 骨,Bip01_* 英文命名)**,因為:
- 遊戲 runtime 本來就用它:`SdoAvatar.Pose()` 是「每根 HRC 骨的 local 旋轉 → FK(`_animWorld`)→ skin」,而 `_mot.Bones[boneIdx]` 就是每骨四元數。MOT → 中樞是**零轉換**。
- MMD↔Biped 的對應表 `BONE_MAP` 已存在(見 §6)。
- 骨數最少、語意最清楚。

> 替代:也可選 Unity Humanoid 當中樞(方案 B,§5)。

## 3. canonical pose 的定義

```
CanonicalPose {
    Quaternion[ ]  localRot;   // 每根 Biped 骨的「相對父骨」local 旋轉(模型空間慣例同 HrcLoader)
    Vector3        rootPos;    // 根(Bip01)位移;其餘骨不帶位移(SDO 慣例:FK 不縮放、位移只在根)
}
```
- 對齊現有 `SdoAvatar.Pose`:它對每骨算 `local`(MOT 有就用 MOT 四元數+pos track,否則用 HRC rest),再 `_animWorld[i] = parent · local`。中樞姿勢 = 「每骨的 local」這一層。
- 之所以挑 local 而非 world:retarget 要逐骨換 rest(見 §7),在 local 層處理最自然。

## 4. 介面設計

```csharp
// 動作來源:把某時間的動作算成中樞姿勢
interface IPoseSource {
    void Sample(float timeSec, CanonicalPose outPose);   // 寫入 Biped local 旋轉 + root 位移
    float Length { get; }
}
// 接收端:用中樞姿勢驅動一個模型 rig
interface IRig {
    void Apply(CanonicalPose pose);   // 把 Biped 姿勢 retarget 到自己的骨架並更新 skin
}
```

來源實作:
- `MotPoseSource`(MOT):幾乎是現況 `SdoAvatar.Pose` 的取樣段抽出來;本來就是 Biped,零 retarget。
- `VmdPoseSource`(VMD):VMD 日文骨 → `BONE_MAP` → Biped；套 rest 補償(§7.1)+ 軸轉換(§7.2)；VMD 腳 IK 需先轉 FK(§7.3)。

接收端實作:
- `SdoRig`:現有 `SdoAvatar`(HRC Biped)直接吃 `localRot`/`rootPos`,沿用現有 FK+skin。
- `MmdRig`:PMX 模型(Unity `SkinnedMeshRenderer`+PMX 骨),用「Biped → 日文」反向 map + rest 補償驅動;Biped 沒有的 MMD 骨(捩り/twist、裙骨…)留 rest。

執行期組裝:`new Player(source: VmdPoseSource(vmd), rig: SdoRig(avatar))` → 熱舞角色播 VMD;`new Player(MotPoseSource(mot), MmdRig(pmx))` → MMD 模型播 MOT。

## 5. 兩種落地方案

| | 方案 A:自訂 Biped 中樞 | 方案 B:Unity Humanoid(Mecanim) |
|---|---|---|
| 中樞 | HRC Biped local 旋轉(本文件 §3) | Unity Humanoid 肌肉空間(內建) |
| retarget | 自己寫(rest 補償等,§7) | Unity 自動 |
| SDO 角色 | **沿用現有 `SdoAvatar` 自訂 skinning** | 需改成標準 Unity rig + 建 Humanoid Avatar |
| MMD 模型 | 寫 PMX→Unity 匯入 + Biped 驅動器 | 設成 Humanoid(可參考 MMD4Mecanim) |
| 動作 | VMD/MOT → Biped local | VMD/MOT → `HumanPose`/AnimationClip |
| 優點 | 重用最多、控制權最大、與現有相機/DPS 無縫 | retarget 交給引擎、業界標準、外部參考多 |
| 缺點 | retarget 數學要自己寫對 | 角色 rig 要標準化(自訂 skinning 重做一層) |

**建議**:先走**方案 A**(最快、重用現有 SDO 程式);第一塊做 `VmdPoseSource` 讓熱舞角色播 VMD。若日後要真正 plug-and-play + MMD 外觀且願意把角色 rig 標準化,再評估遷移到 B。離線的 `vmd_to_mot_blender.py` 仍可作為「正確答案」對拍驗證 A 的 runtime retarget。

## 6. 骨骼對應(已存在)

`H:\bms\tools\bms_sdo\mot_pipeline\vmd_to_mot_blender.py` 的 `BONE_MAP`(MMD 日文 → SDO Biped),節錄:

```
センター→Bip01(根位移)  下半身→Bip01_Pelvis  上半身→Bip01_Spine  上半身2→Bip01_Spine1
首→Bip01_Neck  頭→Bip01_Head
左肩→Bip01_L_Clavicle 左腕→Bip01_L_UpperArm 左ひじ→Bip01_L_Forearm 左手首→Bip01_L_Hand(右側對稱)
左足→Bip01_L_Thigh 左ひざ→Bip01_L_Calf 左足首→Bip01_L_Foot(右側對稱)
```
引擎怪癖(已在腳本處理,runtime 要照搬):
- `Bip01_Spine`(idx2)的旋轉會被引擎當整體骨架基準 → 上半身的彎腰**改由 Bip01_Spine1 承擔**(上半身2→Spine1),Bip01_Spine 保持 rest。
- `Bip01_Neck` 會連動鎖骨/手臂(寫 Neck 旋轉時要注意)。

`MmdRig` 需要這張表的**反向**(Biped → 日文);Biped 沒對到的 MMD 骨留 rest。

## 7. 關鍵難點(不分方案都要對)

### 7.1 Rest pose 補償(最關鍵)
不同 rig 綁定姿勢不同,**不能直接 copy local 旋轉**。逐骨:
```
R_target_local = restDelta · R_source_local · restDelta⁻¹      // 旋轉換到目標骨 rest 基底
   其中 restDelta = restTarget_local · restSource_local⁻¹
```
(實作時以實際慣例為準:可能是 `R_target = restTarget · (restSource⁻¹ · R_source)`,需對拍 `vmd_to_mot_blender` 的輸出校正。)`VMD_TO_MOT_GUIDE.md` 圖中的「+ rest 套用」就是這一步。**先把這步做對,其餘都好辦。**

### 7.2 座標系
- MMD:左手、Y-up、+Z 前。Unity:左手、Y-up、+Z 前 → **MMD↔Unity 幾乎直通**(可能只差個別軸/手性微調),比「MMD→SDO 經 Blender 右手」少一層。
- 但 SDO HRC/MOT 在引擎裡的慣例見 `HrcLoader`(D3D row-major → Unity column-vector,transpose、不翻 X)。VmdPoseSource 產出的 Biped local 要落在**與 MOT 相同**的慣例。

### 7.3 IK → FK
VMD 腳是 IK 控制(`左足ＩＫ`),中樞是純 FK。兩條路:
- **離線烤**:沿用 `vmd_to_mot_blender`(Blender+mmd_tools)把 IK 烤成 FK(現成、品質好)。
- **runtime 解 IK**:在 `VmdPoseSource` 內解兩段式腿 IK(較多工,但免離線步驟)。

### 7.4 骨數差
MMD ~100+ vs Biped 55。MMD→Biped 會併骨(上半身+上半身2→Spine1)、丟棄(捩り/twist);Biped→MMD 是「少驅動多」,twist/裙骨留 rest。品質取決於 map;手指若要,需擴充 `BONE_MAP`(Biped 有 Finger0..)。

### 7.5 比例/骨長
不同模型骨長不同。**旋轉可直接轉**;**位移(根、IK 落點)要按比例縮放**(target/source 骨長或身高比),否則步距/手位會跑掉。

## 8. 實作階段(建議順序)

1. **`VmdLoader`(純解析,可單元測試)**：依 `mmd_vmd_format.md` parse bone/camera/morph;Shift-JIS 名稱;依 (boneName, frame) 排序。對拍 `vmd_reader.py`。
2. **`VmdPoseSource`(VMD→Biped local + rest 補償 + 軸轉換)**：純骨骼數學,單元測試對拍 `vmd_to_mot_blender` 的 .mot 輸出。
3. **抽 `IPoseSource` / 接到 `SdoAvatar`**：讓**熱舞角色直接播 VMD**(第一個可見成果)。IK 先用離線烤過的 VMD 或先忽略手腳細修。
4. **相機 VMD**：`VmdCameraPlayer` 驅動現有 perspective SceneCam(可與 1–3 並行,見 dance-vmd.md)。
5. **`MmdRig` + PMX 匯入**：PMX→Unity mesh+骨(參考 `pmx_reader.py`);Biped→日文反向驅動 → **MMD 模型播 MOT**。
6. （選）評估方案 B(Humanoid)是否值得遷移。

## 9. 參考

- 離線 pipeline:`H:\bms\tools\bms_sdo\mot_pipeline\vmd_to_mot_blender.py`(retarget 正解)、`runner.py`、`single_mot_dps.py`;`H:\bms\tools\bms_sdo\{vmd_reader,pmx_reader,mot_player,mot_writer,mot_core}.py`
- 格式:[mmd_vmd_format.md](../reverse-engineering/mmd_vmd_format.md)、[MOT_HRC_FORMAT.md](../reverse-engineering/MOT_HRC_FORMAT.md)、[VMD_TO_MOT_GUIDE.md](../reverse-engineering/VMD_TO_MOT_GUIDE.md)
- 現有程式:`65/My project/Assets/Scripts/Game/{SdoAvatar,MotLoader,HrcLoader,MshLoader}.cs`
- 外部參考(方案 B):MMD4Mecanim(VMD→Humanoid)、各 Unity-MMD 專案
