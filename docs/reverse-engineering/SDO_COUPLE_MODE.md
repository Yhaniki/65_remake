# 情侶模式（LOVER / Couple）逆向筆記

> 來源：CN 線上客戶端 `H:/sdo_cn/sdo.bin.c`（27MB Ghidra 反編譯）。
> offline standalone（`assets/sdox_offline/sdo_stand_alone.exe`）**把情侶模式整組砍掉了**
> （mode byte 只 0/1、模式名表是無 `.text` 引用的死資料、無截圖 API），所以真實做法一律以線上版為準。
> 所有斷言標注 `sdo.bin.c:行號` 與信心度；查不到的常數標「需 Frida 實機」。

---

## 1. 畫面與模式派發

有兩個獨立軸：**Screen-ID**（哪個畫面）與 **Mode byte**（畫面內行為）。

- 情侶 gameplay 畫面 = master factory `FUN_00404ec0` 的 **`case 0x12`（18）**，配 `0x250c` bytes 物件、
  ctor `FUN_0072f2a0`（vtable `&PTR_DAT_00af0bfc`）。`sdo.bin.c:16267-16272, 463465`（高）
- **不是 couple 專屬 class** —— 它是唯一的線上 gameplay/replay 畫面（`StateReplay_AU`），內部依 mode byte 分支。
- **Mode byte = 物件 `+0x62`**（Load 時鏡射到全域 `DAT_00c9f2f4+0x88`）。`sdo.bin.c:471420-471424`（高）

| `+0x62` | 行為 |
|---|---|
| `0x02` / `0x0c` | **couple/love**：愛心 HUD + 加心（`469632`、`464934`） |
| `0x01` / `0x07` | 配對跳舞 / 面對面（`464874`、`469513`） |
| `0x06` | pet/baby（`469284`、`471420`） |

- 伺服器權威：房 game-type 全域 `DAT_00c9f2f4+0xd11`；`+0xd11==0x04` → 強制 play-mode `+0x88=0x0c`（couple）。
  `sdo.bin.c:352398-352433, 497851-497864`（高）
- **重製（離線無網路）**：lover 房直接設 mode = `0x0c`，不做封包。

> 殘留：`0x02` vs `0x0c`（都開愛心）、`0x01` vs `0x07`（都開配對）是不同房型或 replay-vs-live 之分，命名未在碼中確定。

---

## 2. 愛心機制（**取代先前所有 web 猜測**）

**愛心是伺服器/replay 事件驅動，不是本地判定計算。**

- 事件佇列 `obj+0x168`（每筆 0x2c bytes），`FUN_0072fce0` 反序列化後 `switch` 分派。`sdo.bin.c:464561-464563`（高）
- **唯一加心點 = `case 0xc`（event id 12）**，gate `+0x62 ∈ {0x02,0x0c}` 且 `param < 0xe0`：
  - 位址 `obj + 0x1994 + ((param&1)+(param>>1)*2)*4` —— 該算式恆等於 `param`，故 **slot = param**、位址 = `+0x1994 + param*4`。
  - `if (*count < 0x14) *count = *count + 1;` —— **每位舞者上限 20（0x14）硬夾，無 else、無 fail path**。`sdo.bin.c:464933-464944`（高）
- 事件子碼只設「慶祝/wink」旗標、**不動計數**：`param >= 0xf0` → row-A 滿（`obj+0x19b8+i`）；`0xe0..0xef` → row-B 滿（`obj+0x19bb+i`）。`sdo.bin.c:464947-464951`（高）

### 上限/勝負
- **上限 = 20，不是 31**；超過只是不再 +1。`sdo.bin.c:464941-464942`（高）
- **愛心路徑無任何勝負判定**（非 31、非領先差、無 hug/kiss 分級）；勝負沿用一般模式的分數定格。（愛心無門檻＝高；沿用分數＝中）
- 易誤讀：`464335` 的 `0x31`(=49) 是 `obj+0xfa0` 動畫幀索引，**與愛心無關**，別當上限。（高）

### 送出端（未定位）
哪個判定等級（Perfect/Great/Cool）或 combo 里程碑會「送出」id 0xc，其序列化端在此 27MB 內 grep 不到
→ **需 Frida 追線上 judge→network-queue**。離線重製必須自訂（例：每 Perfect 給 1 心，明確標為 remake 決策非還原）。

### HUD
每位舞者 **2 排 × 20 = 40 顆小心**：sprite 陣列 `obj+0x1790` stride `0x2a`（41 顆 `small_heart` + 1 顆 `middle_heart`）；
render 依計數 `0x1994`(A) / `0x1998`(B)。sprite：`Hearts\{big_heart,big_heart_f,small_heart,middle_heart,small_heart_wink}.an`。
`sdo.bin.c:464062-464129, 469661-469796`（高）

---

## 3. 結尾定格 + 拍照

### 不存檔截圖（決定性）
整個 27MB **D3D 抓圖 API = 0 命中**（`GetFrontBufferData/GetRenderTargetData/D3DXSaveSurfaceToFile/GetBackBuffer/StretchRect` 全無）。（高）
→ 「拍照」= **腳本相機運鏡 + 相框 overlay 疊在擺 pose 的舞者上**，非影像檔。

### 三顆相機（`FUN_0073c690` @ `471181`，先掛 `Datas\Camera.bin`）
| 欄位 | 來源 | 角色 |
|---|---|---|
| `obj+0x19c0` | 硬編 float（`FUN_00414350`） | 預設遊戲相機 |
| `obj+0x19c4` | `Couple\TakePhoto.cv` | **最終拍照運鏡** |
| `obj+0x19c8` | `Couple\lovertest.cv` | 曲中情侶相機 |
`sdo.bin.c:471516-471541`（高）

相機狀態機（`464974, 465229-465238, 465302-465310`，高）：
- `case 0xd`（finish）→ 播 `+0x19c0`，舞者進勝負定格 pose
- `case 0xf` → 收尾（預設 or lovertest，依 `+0x144c`）
- `case 0x10` → **播 `TakePhoto.cv`（`+0x19c4`）= 拍照瞬間** → 疊 `PhotoFrameDlg` 相框

`TakePhoto.cv` 實測：CV002、24000 keyframes、pos t=0..54.03、target 恆 `(0,1,0)`（看兩人腰高原點）。（高，byte-exact）

### 相框 / 結果
- `PhotoFrameDlg\`：3 變體（`PhotoWin1/2/3`），各 4×3 格 256² 拼 800×600 全螢幕邊框；**選哪個變體的規則未追到**。`sdo.bin.c:463408`（高 / 規則未定位）
- `WinLover` = `Lover1-6.an` 排 3×2 pose 網格（`GAMEPLAYLOVE.XML`）；分級獎勵 `WINLOVER0-5.XML`（紅包/花/巧克力），**選階輸入未定位**。（資產＝高、規則＝未定位）
- **另有獨立婚禮畫面** `WenddingMode\ShowLove.xml`（`FUN_007be060` @ `524202`），與情侶 gameplay（0x12）**不同畫面，勿混淆**。（中）

### End-of-song 順序（依 case id 重建，中偏高）
`0xd` finish 定格 → `0xf` 收尾運鏡 → `0x10` TakePhoto 掃鏡 + 相框 → `WinLover` + `WINLOVER0-5` 獎勵。

---

## 4. 男女面對面站位 —— **真的相對旋轉，非相機錯覺**（高）

- 每位舞者 avatar `+0x60` 存**面向角（度）**；`FUN_009307c0` → Y 軸四元數（軸 `(0,1,0)`，角 = `+0x60 × 0.0174533`），
  寫進主身節點 `+100/0x64`（**整個舞者實體旋轉**）。`sdo.bin.c:613983-614058, 709124`（高）
- 面向角來自 per-slot ushort 表 `DAT_00b86c64`（type6）/ `DAT_00b86c70`（其他），配對兩人取不同角 → 轉向彼此。
  配對 = `obj+0x146c`(男)/`0x1470`(女)。`sdo.bin.c:469514-469517, 464888-464912`（高）
- 額外站位偏移 `-10`（`+0x88∈{0x02,0x01,0x07}` 且 `+0xd11==0`）；子節點 `-15 x / -10 z`。`sdo.bin.c:614071-614074, 469491-469497`（中）

> **精確角度值 `.c` 讀不到**（無 PE `.rdata`）→ 需真 EXE VA `0x00b86c64` hexdump 抽 6 ushort（0/180 或小角 toe-in 未知）。先用可調佔位。
> 子模式細節：面對面 gate `+0x62∈{0x01,0x07}`、收心 gate `+0x62∈{0x02,0x0c}` —— 同一 byte 不同值；重製設 `0x0c` 才能同時拿配對＋愛心（需確認兩相是否併存或切換）。

---

## 5. `.cv` 相機格式（loader `FUN_00415050` @ `27075-27362`）

```
Header: [0..11] 12B ASCII magic "CV000000000N"  (N=2/3/4)
        [12..15] int32 timebase
        [16]     flag byte
        [17..20] int32 keyframe count
Body:   positions[count]  每筆 16B = vec4(x,y,z,time)   ← 第4 float 是遞增時間戳
        targets[count]    每筆 12B = vec3(x,y,z look-at)
        trailing          1× 16B vec4 (up/pivot)
```
- `speed scalar = count / timebase`（例 24000/500 = 48）。`sdo.bin.c:27215`
- **CV002 已 byte-exact**（`TakePhoto.cv`：magic `CV0000000002`, timebase 500, count 24000 → `12+4+1+4+24000*0x1c+0x10 = 672037` = 檔案大小）。（高）
- **CV004**（`LOVERTEST.CV` / `CAMERA/LOVERS/*/NNN.CV`）加 const 最佳化，byte 打包差 ~186B（欄位語意確定、需再一輪 decode）。（中）
- `.CDT` = 文字索引：count + `path:timebase:flag`（例 `lovers\cam0005\000.cv:3000:0`）。（高）
- 重製已有 `CvLoader.cs` / `CdtLoader.cs`（`65/My project/Assets/Scripts/Game/`）可沿用。

---

## 6. 素材（全在線上樹 `assets/Datas/`，offline 無）

| 用途 | 路徑 |
|---|---|
| 愛心 HUD | `UI/HEARTS/{BIG_HEART,BIG_HEART_F,SMALL_HEART,MIDDLE_HEART,SMALL_HEART_WINK}.AN` |
| 相框 overlay | `UI/PHOTOFRAMEDLG/PHOTOFRAME0-35.AN/.DDS` + `PHOTOFRAMEDLG.XML` |
| 拍照/曲中相機 | `CAMERA/COUPLE/{TAKEPHOTO,LOVERTEST}.CV` |
| couple HUD/logo/pose | `UI/GAMEPLAY/PLAYLOVE/{GAMEPLAYLOVE.XML,LOVER1-6,LOVELOGOMAN,LOVELOGOWOMAN}` |
| 分級獎勵 | `UI/GAMEPLAY/HONGBAO/WINLOVER0-5.XML` |

愛心/loveyou 小圖在 `3DEFT/GENERIC/` 是**通用裝飾**；`CHEERBAND_HEART` 是**加油團**非情侶。

---

## 7. 仍需實機（Frida / PE hexdump）確認

1. 愛心 id 0xc **送出端**：哪個判定/combo 觸發、每擊或每里程碑給幾顆。
2. 面向角 **精確值** `DAT_00b86c64/6a/70/74`（每組 6 ushort，VA `0x00b86c64` hexdump）。
3. `0x02`/`0x0c`、`0x01`/`0x07` 語意差（房型 / replay-vs-live）。
4. 獎勵分級 `WINLOVER0-5` 選階輸入（總愛心 / 分數 / 伺服器）。
5. `PhotoFrameDlg` 三相框變體選擇規則。
6. `obj+0x144c` 旗標語意（切換 TakePhoto↔lovertest）。
7. CV004 byte 打包（`LOVERTEST.CV` 預測 5134 vs 實際 5320）。

---

## 8. 重製實作對映

- 純邏輯：`Sdo.Ruleset/LoverHearts.cs`（本檔第 2 節，夾 20）+ `LoverFacing.cs`（第 4 節，度→四元數）+ 測試。
- 接線：`GameMode` enum 加 `Lover` → `GameSession` → `FrontendApp.StartGameplay` → `ScreenGameplay`。
- 第二舞者：`TryLoadAvatar()` 重構生男舞者面對面（第 4 節站位）。
- 結尾：`FinishSequenceTick` 加 couple 相機序（`CvLoader` 播 `TAKEPHOTO.CV`）+ 相框 overlay（第 3 節，不截圖）。
- worktree：`H:\65_remake-couple`（分支 `feat/couple-mode`）。
