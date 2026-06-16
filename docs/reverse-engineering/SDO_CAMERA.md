# SDO 遊玩相機系統（CameraMgr / CDT 導播 / CV 鏡頭路徑）反編譯考據

> 來源：`assets/sdox_offline/named/modules/render/004_camera_0040a420.c`（`CCamera` / `CCameraMgr`）、
> `modules/gameplay/021_gameplay_0046b8a0.c`（遊玩狀態機、相機模式切換）、
> `assets/sdox_offline/Extracted/CAMERA/*.CDT` + `*/CAM####/###.CV`。
> remake 對應：`65/My project/Assets/Scripts/Game/CvLoader.cs`、`CdtLoader.cs`、`Step1Game.cs`。

遊玩相機 = 一份 **CDT 導播序列**（自動運鏡），玩家可用 F2 在「自動導播」與「6 顆固定鏡」間切換。

---

## 1. 物件與序列結構

| 物件 | 角色 |
|------|------|
| `CCamera`（`Camera_ctor_default_0040d180` / `Camera_ctor_FromFile_0040db80`）| 單一鏡頭：靜態 eye/target，或從 `.CV` 載入的動畫路徑 |
| `CCameraMgr`（`CameraMgr_ctor_0040d810`，單例 `DAT_00674ed0`）| 管理目前作用中的鏡頭 + 4 條序列清單 A/B/C/D |

`CCameraMgr` 內的關鍵欄位（byte 偏移）：

| 偏移 | 意義 |
|------|------|
| `+0x10` | **作用中鏡頭指標**（`Camera_Update` 每幀用它算 view/proj） |
| `+0x2e4 / 0x2e8` | 序列 **A**（主導播，預設）清單 begin/end；index `+0x350`、count `+0x34c` |
| `+0x300 / 0x304` | 序列 **B**（特殊：param_2==9 / 0x3e）；index `+0x358`、count `+0x354` |
| `+0x318 / 0x31c` | 序列 **C**（param_2==0x40/0x41/0x42）；index `+0x328`、count `+0x324` |
| `+0x32c / 0x330` | 序列 **D**（param_2==0x71/0x72）；index `+0x33c`、count `+0x338` |
| `+0x340/0x344/0x348` | **anchor**（定位點）；`Camera_Update` 推進作用鏡頭的 `0xc8/0xcc/0xd0` |
| `+0x2fc` | 序列作用中旗標 |
| `+0x2fd` | **暫停**旗標（1=暫停自動序列＝固定鏡模式） |

序列推進：`CameraSeq_AdvanceA_0040cbd0`（B/C/D 同理）→ index+1、超過 count 回 0（**循環**），選下一顆鏡頭、把它的播放幀 `0x28=0`、起始時間 `0xc0=now`、設為作用鏡頭。每顆鏡頭播完由註冊在 report list 的 delegate 觸發 AdvanceA。

---

## 2. CV 鏡頭路徑檔格式（`Camera_ctor_FromFile_0040db80`）

magic `CV0000000002` / `CV0000000003` / `CV0000000004`（CV3、CV4 解析碼**逐位元組相同**；CV2 少一個 eye 旗標）。

```
char[12]  magic              "CV0000000003"
u32       frameCount         （≒ 該鏡頭時長 ms，見 §4）
u8        eyeFlag   -> cam+0x10   eye 是否單一靜態 key
u8        moveFlag  -> cam+0x11   target 是否單一靜態 key
u32       keyCount
eye[16B   * (eyeFlag ? 1 : keyCount)]    xyz + pad
mid[12B   * (...)]                       （中間控制點，remake 略過）
target[16B* (moveFlag? 1 : keyCount)]    xyz + pad
```

`Camera_GetEyePos_0040c870` / `Camera_GetTargetPos_0040c7c0` 取值：

```
if (cam+0x12 != 0)          // CDT 第三欄旗標 = 相對 anchor
    eye = anchor(0xc8) + eyeArray[ eyeFlag ? 0 : frame ];
else if (eyeFlag) eye = staticEye;     // 絕對、靜態
else              eye = eyeArray[frame];// 絕對、動畫
```

> 三個旗標互相獨立：`0x10`=eye 靜/動、`0x11`=target 靜/動、`0x12`=相對 anchor / 絕對世界。

`Camera_Update_0040c920` 每幀：`MatrixLookAtLH(eye, target, up=0x8) → MatrixPerspectiveFovLH → 算 frustum`。

### ⚠️ anchor = 舞者的「定位點」(不是 0；舊文件誤記)
`021_gameplay_0046b8a0.c:7375-7391` **每幀**把 CameraMgr `0x340/0x344/0x348` 設成**作用中舞者的世界座標** `dancer.0x6c/0x70/0x74`（平滑後）。該座標來自靜態表 `@0x582690`，索引 `(slot + mode*6)*0x48`，每筆 6 個地板點（Y=0）：

| 人數(entry) | 第 1 點(本地舞者) | 其餘 |
|---|---|---|
| **1(solo)** | **(0,0,0)** | — |
| 2 | (-25,0,0) | (-50,0,50) |
| 3 | (0,0,0) | (-50,0,50)(50,0,50) |
| 6 | (-25,0,0) | (-100,0,50)(100,0,50)(-50,0,50)(50,0,50)(0,0,0) |

所以 `0x12=1`(相對)鏡頭 = **anchor(舞者點) + keyframe**；`0x12=0`(絕對)鏡頭 = keyframe 原值。**獨舞時 anchor=(0,0,0)**，相對與絕對才剛好都等於世界原值——但前提是**舞者站在原點**。

### 6 顆固定鏡 = 硬編碼靜態 eye/target（map 無關）
`021_gameplay ~5512` 用 `Camera_ctor_default(DAT_005824f0[i], DAT_00582538[i])` 建 6 顆**絕對靜態**鏡（`0x12=0`）。從 exe `.data` 解出：

| cam | eye | target |
|---|---|---|
| 0 | (-3,46,-181) | (-2,38,21) |
| 1 | (-96,85,-126) | (-11,38,66) |
| 2 | (147,97,-85) | (-29,38,110) |
| 3 | (-3,163,-154) | (-2,38,21) |
| 4 | (-1,**476**,-60) | (-2,38,21)（高空俯視） |
| 5 | (-4,38,**-346**) | (-2,38,21)（遠後拉） |

---

## 3. CDT 導播清單格式（`CameraMgr_LoadCamerasBin_0040e0e0`）

`Datas\Camera.bin` 內的 `<NAME>.CDT`：`u32 count`，後接 `count` 個 NUL 結尾字串，每筆

```
相對路徑:時長ms:旗標     例 "1\cam0005\001.cv:13000:0"
```

- 路徑 = `CAMERA/` 下的 `.CV`（反斜線分隔）。
- 時長ms = 該鏡頭播放時間（≒ CV 的 frameCount）。
- 旗標 = 上面的 `0x12`（相對/絕對；跳舞場景無作用）。

`1.CDT` 第 0 筆 = `1\cam0005\001.cv:13000:0`，其 eye keyframe 由 **(-41.9, 1306.4, -469.3)** 高空後方，13 秒俯衝至 **(-16.2, 50.7, -154.4)** 膝高貼近（`|eye-target|` 1373→199）——**這就是官方開場的 crane-down 俯衝鏡，是原版設計、忠實存在**。

---

## 4. 播放時序

`frameCount == 時長ms`（實測：`cam0005/001` 13000、`cam0002/022` 18000、`cam0000/000` 4000 都與 CDT 時長相符）。鏡頭幀 index 由 `(now - 起始時間 0xc0) × (keyCount/frameCount)` 推進，即整段 keyCount 在 `frameCount` ms 內播完。remake 用 CDT 時長取樣 `t = elapsed/duration` 等效。

---

## 5. 模式切換狀態機（`021_gameplay_0046b8a0.c`）

### 開場（`~6747`）
```
LoadCamerasBin(主清單 A) ; LoadCamerasBin(特殊清單) ; CameraSeq_Begin(序列A index0)
if (KeyCfg_GetByte5cd != 0) { mode=0; ChangeCamera(fixed0); }   // 使用者設定「固定鏡」-> 開場不播 crane
```
> `KeyCfg_GetByte5cd_004220c0`（key config byte `0x5cd`）= 玩家的「相機」偏好；開啟時開場直接固定鏡 0，不做俯衝。

### F2 切換鏡頭（cmd `0x3c`，`~2285`）
```c
mode = (mode==-1)? 0 : mode+1; if (mode>5) mode=-1;   // -1=自動導播, 0..5=固定鏡
if (mode == -1) { CameraSeq_SetPlaying(0); return; }  // 切回自動：見下
CameraSeq_SetPlaying(1);                               // 暫停自動序列
ChangeCamera( fixedCam[ this+0x44 + mode*4 ] );        // 設固定鏡（6 顆靜態 eye/target）
```
6 顆固定鏡在 `~5512` 用 `Camera_ctor_default(DAT_005824f0[i], DAT_00582538[i])` 建立 = **靜態 eye/target**（非動畫，map 無關）。

### 切回自動（`CameraSeq_SetPlaying_0040ccd0(0)`）— 關鍵
```c
paused = 0;
if (序列作用中) { if (index!=0) index--; AdvanceA(); }   // 退一格再進 = 重播「當下凍結的那顆」
```
**絕不會回到 shot 0（開場 crane）**；它接著播切走前所在的那顆鏡頭（固定鏡期間序列是凍結的）。

---

## 6. 不同地圖 / 人數 → 不同 CDT（確認：是）

`LoadCamerasBin(param_1, param_2)` 用 `param_2` 索引 `(&PTR_s_6_cdt_00581478)[param_2]`（CDT 檔名表）。Ghidra 把 switch case 收成空 `break`，但**反組譯機器碼**(`021_gameplay ~0x4768a9`)還原出完整對照——`switch(mapId-3)` 由 `playerCount`(`[ebx+0xf8]`) 分桶，兩張跳表：

```
playerCount <= 3 :  jmp [ (mapId-3)*4 + 0x4780b8 ]   // 推 _1(獨舞)變體
playerCount 4..6 :  jmp [ (mapId-3)*4 + 0x4780f8 ]   // 推 base(團體)變體
playerCount  > 6 :  push 0 (6.cdt)
出界 fallback     :  獨舞→ push 6(1.cdt) / 否則 push 1(3.cdt)
```

**mapId → CDT（解出的完整對照，mapId = 場景 id）：**

| mapId | 場景 | 獨舞(<=3) | 團體(4-6) |
|---|---|---|---|
| 3 | box | Garage_1 | Garage |
| 4 | sea | sea_1 | sea |
| 5 | christmas | Christmas_ | Christmas |
| 6 | playground | playground_ | playground |
| 7 | sky | sky_1 | sky |
| 8 | egypt/金字塔 | egypt_1 | egypt |
| **9** | **guatan = 宮殿 (SCN0009)** | **palace_1** | **palace** |
| 10 | huache | huache_1 | huache |
| 11 | — | (fallback 1/3) | (6.cdt) |
| 12,13 | fifa | fifa_1 | fifa |
| 14 | ocean/海底 | ocean_1 | ocean |
| 15 | ghosthill/墳墓 | Ghosthill_1 | Ghosthill |
| 16 | street | street_1 | street |
| 17 | railway/地鐵 | railway_1 | railway |
| 18 | houseboat/船 | houseboat_1 | houseboat |

`s_6_cdt` 表完整 index（0=6.cdt, 1=3.cdt, 6=1.cdt, 0x16=palace_1, 0x17=palace …）共 115 筆，由 exe `.data @0x581478` 指標表解出。其餘模式 CDT（2v2v2/3pk3/lovers/cairen/q_*/wedding_* 等，index 2-8、0x26+）由各自模式的選擇邏輯使用。

> **關鍵教訓**：SCN0009 是**宮殿**，official 用 `palace_1.cdt`（crane 起點 eye Y≈338，在場內），**不是**通用 `1.cdt`（crane Y≈1306，在 628 屋頂之上＝跑到場外）。remake 早期誤用 1.cdt 正是「default 從場外飛進來 + 角度全錯」的根因。

---

## 7. remake 落差與修正（`Step1Game.cs`，已驗證）

忠實模型（獨舞）：**舞台原生座標 + 舞者站 dance-spot(0,0,0) + 鏡頭 verbatim**。

| 項目 | 原版 | remake 修正前(錯) | 修正(對) |
|------|------|--------------|----------|
| **CDT 選擇** | 反組譯跳表：SCN0009→**palace_1.cdt** | 寫死 `1.CDT`（crane Y1306 跑場外） | `SoloCdt/GroupCdt` 完全照跳表，由 `SceneMapId()` 取 mapId |
| 舞者定位 | feet 站 dance-spot 表座標 | chest 釘在 `_avatarChest`(相對偏移當世界點) | feet 站 `_danceSpot`(solo=(0,0,0)) |
| 鏡頭座標 | `eye/target` verbatim；`:1` 才 `+anchor` | 一律 `off = _avatarChest-target[0]` 重置中心 | verbatim；`:1` 加 `_danceSpot`、`:0` 原值 |
| 6 固定鏡 | 硬編碼靜態 eye/target | 用 6 個 `.CV` 取樣近似 | **直接用解出的 `FixedEye/FixedTgt` 表** |
| 切回 default | `SetPlaying(0)`：shot0→shot1（不回 crane） | 硬 `_dirShot=0` 重播 crane | `if(_dirShot==0) _dirShot=1`（照反編譯） |
| CV magic | CV2/3/4 | 只收 CV3 | 加收 CV4 |

### 根因（已用機器碼證實）
「default 從場外飛進來 + 角度全錯」= remake 用了**錯的 CDT**（通用 `1.cdt`，crane eye Y≈1306 在 628 屋頂之上）。SCN0009 是宮殿，official 用 **`palace_1.cdt`**（crane eye Y≈338 在場內）。改用正確 CDT + verbatim 模型後，開場 crane 在宮殿內俯衝、6 固定鏡角度與官方一致、切回 default 為場內鏡。`KeyCfg byte 0x5cd`「固定鏡」偏好開啟時則開場直接固定鏡 0、不播 crane。

### 場地渲染：single-sided 背面淘汰（不是 double-sided）
`scene.hrc` root = identity（場地無 transform，原生座標）。`SceneLoader` 原本畫成 **double-sided**，但原版 D3D **背面淘汰**。固定鏡 5（eye z=-346）在場地後方柱列（z≈-300，朝內）**之後**，double-sided 會畫出柱子背面→整個擋住；single-sided 淘汰背面→看穿柱背、柱子落在右側、舞者可見（與官方一致）。winding 用**原值**（verts 沒 X-negate，D3D9-LH 與 Unity 同為 CW-front）；cam0-4 沒裡外反證明 winding 正確。此修正同時讓所有「鏡頭在牆後/上方」的角度（crane 俯視等）正確看穿牆面。
