# 放 MMD 模型進遊戲 (DATA/MODEL)

## 怎麼加一個模型

一個資料夾 = 一個模型。把整包解壓進去、保持它原本的結構就好:

```
assets/MODEL/<模型名>/xxx.pmx        ← 開發樹 (編輯器直接讀)
assets/MODEL/<模型名>/textures/…       模型自己的 textures/Toon/Sph 原樣保留

<exe>/DATA/MODEL/<模型名>/xxx.pmx     ← 打包後的遊戲 (玩家自己加模型也是丟這裡)
```

`tools/package_build.ps1` 會把 `assets/MODEL` 整棵複製成 `DATA/MODEL`,所以**編輯器能跑的模型,打包出來的 dance.exe 也能跑**(在此之前打包版根本找不到模型,開了也沒反應)。

掃描規則在 `MmdModelCatalog.cs`(純邏輯,有單元測試):

* 「直接放著 .pmx 的資料夾」就是一個模型,資料夾名 = 顯示名稱。
* 往下找兩層,所以 `MODEL/某某整合包/miku/miku.pmx` 也掃得到。
* 有 .pmx 的資料夾就是葉子,不再往裡面找 → 模型自己的 `textures/`、`Toon/` 不會變成假模型。
* 一個資料夾裡有 `-JP`/`-EN`/`-CN` 多個 .pmx = 同一個模型的語系版本,**自動挑 JP**(理由見下)。
* 舊的單一模型路徑 `assets/IkaHatunemiku2025/` 仍然有效,不用搬。

## 遊戲裡怎麼用

**開場(選性別)畫面左邊的設定面板 → 展開 → 「MMD」分頁。**改完按「儲存設定」就寫進 `config.ini`,下次開遊戲還在。
拉滑桿/切開關是**當場生效**的(不用重開、不用退出面板),存檔只是把值留住。

| 設定(config.ini `[Mmd]`) | 作用 |
|---|---|
| `mmdEnabled` | 總開關:SDO 原角色 ⇄ MMD 模型 |
| `mmdModel` | 用哪個模型(= `DATA/MODEL` 底下的資料夾名;留空 = 掃到的第一個) |
| `mmdPhysics` | 頭髮/裙擺布料模擬。**嫌進場慢就關這個** —— 布料是建一隻 MMD 角色最貴的一段 |
| `mmdGravity` / `mmdStiffness` / `mmdColliderScale` | 布料手感:重力倍率 / 硬度 / 身體碰撞半徑倍率 |
| `mmdToon` / `mmdOutline` / `mmdSphere` | 卡通著色 / 描邊 / sphere 反光 |
| `mmdFlipV` | 貼圖 V 翻轉(某個模型貼圖上下顛倒時關掉) |
| `mmdAim` / `mmdRootMotion` | 重定向方式 / 根骨位移(診斷用,平常都開著) |

指令列:`dance.exe -mmd -mmdmodel <名稱>` 開場就進 MMD 模式並指定模型(名稱可以只給片段,例如 `-mmdmodel miku`)。
指令列只影響**這次啟動**,不會寫回 config.ini。

每個模型只解析一次並快取,所以在面板上來回換模型不會重複付解析成本。

> 改版前這一整組是遊戲裡一塊自己畫的 IMGUI 除錯面板(F7 切換 / F9 換模型 / F10 開關面板),值只活在記憶體、
> 關掉遊戲就沒了。現在全部搬進設定面板 + `config.ini`,那三顆鍵與那塊面板都拿掉了。

## 載入成本(實測,打包版 dance.exe)

一個模型只有兩段是「重的」,而且兩段都是**整個 process 只做一次**:

| 階段 | 時間 | 範圍 |
|---|---|---|
| 解析 .pmx | ~100 ms | 每個模型一次(依 .pmx 路徑快取) |
| 共用 mesh + 材質 + 貼圖 | ~450 ms(其中 ~95% 是十張 2048² PNG 解碼) | 每個模型一次 |
| **每一隻舞者自己的 rig** | **~10 ms** | 每隻(骨架 + 布料) |

所以「換場景要重新讀取」不是每次換場景真的重讀,而是那 ~550 ms 落在**第一次有 MMD 角色出現的當下**
(rig 是跟著舞者生成的 —— 進房間、進歌、開性別選擇畫面)。三件事處理掉它:

1. **開機預熱**:`mmdEnabled=1` 時開機就把解析與共用資產做掉,藏在本來就有的開機載入畫面後面。
   用**時間預算**分幀(150 ms/幀,見 `MmdAvatarSwap.PrewarmBudgetMs`),不是一張貼圖一幀 ——
   開機那幾幀在掃歌、本來就長達 ~500 ms,讓太多次反而更慢(量過:一張一幀 = 10.5 秒還沒做完,預覽先生成 → 預熱白做)。
2. **共用資產釘住不卸載**:mesh / 材質 / 貼圖都掛 `HideFlags.DontUnloadUnusedAsset`。
   結算「重玩」走 `SceneManager.LoadScene`,它會跑 `Resources.UnloadUnusedAssets`,
   而這些是「只有 static 欄位參照著」的執行期資產 —— 不釘住就會被回收,下一隻舞者整包重付。
   (回歸測試:`MmdSharedAssetTests`,它真的跑一次 `UnloadUnusedAssets` 再檢查資產還在。)
3. **關掉就是 0 成本**:`mmdEnabled=0` 時連 .pmx 都不會去解析(以前是進房間就無條件解析一次)。

實測前後(打包版,開機到性別選擇畫面):第一隻 MMD 角色 **518 ms → 65 ms**,第二隻之後 8~12 ms。

嫌還是慢就關 `mmdPhysics`:布料求解是每隻 rig 裡最貴的一段,關掉時整組不建(不是建了再關)。

## 多人連線:別人看得到你的模型

開著 `mmdEnabled` 進房間,你的模型會自動上傳到 server,同房的人就看得到 —— 反過來你也會自動去拉別人的。

**沒有模型的那段時間看到什麼:他的 SDO 穿搭。** 這不是退化的替身畫面 —— MMD 模型本來就是疊在 SDO 骨架上顯示的
(SDO 那隻永遠是動作驅動器),所以「還沒下載完」的正確畫面天生就是他的穿搭。模型到了之後**當場換身體,不重建角色**:
位置、朝向、正在播的動作全都留著,不會瞬移也不會有一幀空白。

| 設定 | 作用 |
|---|---|
| `mmdShareModel` | 1(預設)＝把自己的模型上傳分享。關＝別人看到你的 SDO 穿搭(你自己畫面上仍然是 MMD) |
| `mmdEnabled` | **同時也是這整條的總開關**:關掉 = 不上傳、不下載、不查詢,零流量 |

⚠️ **使用規約**:網路上流通的 MMD 模型多半帶規約,有些明確禁止再配布。`mmdShareModel` 就是為此存在的 ——
不確定就關掉。(模型包裡的 `readme.txt` 會跟著一起傳,規約不會被留在你這邊。)

### 怎麼運作的

* 模型的身分是**內容指紋** `packId`(`ModelPackId`,全檔 SHA-256),與外部歌用的是同一套機制與同一條傳檔管線
  (`kind=model`)。同一份模型在兩台機器上算出同一個 id → server 已經有就是**零上傳**,每次進房不會重傳。
* 你的 `packId` 放在外觀裡(`setLook` 的 `mmd` 欄位)跟著房間快照廣播 —— 它就是外觀的一部分。
* 別人傳來的模型放 `DATA/MODEL/.net/<hex>/`。開頭的點讓它**不會出現在設定面板的模型清單裡**
  (那是別人的模型,不是你裝的)。
* 下載回來會自己重算一次 packId,對不上就整包丟掉 —— 不把「server 一定是好的」當前提。
* **歌永遠優先**:缺歌會擋住整場比賽,模型只是外觀。所以只要有歌在傳,模型這條就一步都不動。

### 安全性(server 端重驗,一項都不信 client)

* 模型有自己的白名單(`.pmx`/貼圖/`.ini`/`.txt`),與歌曲那張**分開** —— 聯集起來就等於
  「歌曲資料夾可以挾帶 .pmx、模型資料夾可以挾帶 mp3」。執行檔/壓縮檔/影片一律擋。
* 一包沒有 `.pmx` 的貼圖不是模型,拒收。
* **只能上傳你身上穿的那一個**(＝你自己宣告的 `mmd`)。少了這條,任何連上來的人都能把 server 當免費檔案空間用。
* 同一個 `packId` 不能改換 kind 再傳一次。

## 換模型時什麼是自動的、什麼會出事

**自動(不用改任何程式碼)**

* **身高**:量模型自己的高度,縮放去對 SDO 骨架 —— 高矮胖瘦的模型都對得上。
* **物理**:頭髮/裙子/領帶讀**模型自己的剛體與關節**(碰撞形狀、碰撞群組遮罩、關節角度限制、阻尼)轉成 Magica Cloth 2。不是針對初音手寫的。模型沒帶剛體資料時,退回內建的身體碰撞體 + 彈簧骨。
* **貼圖/材質**:sphere(matcap)、toon、描邊都照 PMX 材質旗標走。

**會出事的三種模型**

1. **英文骨名**:重定向表 `MmdBoneMap.cs` 的 key 是 MMD 準標準**日文**骨名(センター/上半身/左腕/左ひざ…)。骨名不是這套 → 沒有骨頭被驅動 → 模型站著不動。同包有 JP 版就自動挑 JP,正是為此。
2. **剛體命名很特別**:`MmdMagicaCloth.GroupOf()` 用剛體名稱的關鍵字把布料分成 4 組(瀏海 `Bang|前髪` / 裙子 `Dress|Skirt|スカート|裙` / 領帶 `Tie|ネクタイ` / 其餘算頭髮)分別套手感參數。名稱對不上不會壞掉,只是全部落進「頭髮」那組,裙子可能偏硬或偏軟 —— 用設定面板的布料重力/硬度即時調。
3. **貼圖 V 翻轉**:少數模型 UV 是反的(領帶/腰帶最容易看出來),設定面板的「貼圖 V 翻轉」關掉。

## 存下調好的物理:physics.ini

每個模型資料夾裡可以放一個 `physics.ini`(跟 .pmx 同一層):

```
assets/MODEL/<模型名>/xxx.pmx
assets/MODEL/<模型名>/physics.ini     ← 有這個檔就用它,沒有就直接從 .pmx 轉換
```

* 這個檔現在是**手寫的**(或由 `MmdAvatarSwap.SaveProfile()` 寫出 —— 布料驗證的 PlayMode 測試就是這樣用)。
  以前是除錯面板上的「存成 physics.ini」/「刪除」兩顆鈕,面板拿掉後那兩顆鈕也沒了;設定面板是一張
  「key = 值」的表,放不下這種寫檔動作。每一行都有中文註解,照著改就好。
* 想回到純轉換值:直接把 `physics.ini` 刪掉。
* 覆寫是**逐 key** 的:只寫兩行「把裙子調軟」也可以,其他沒列到的 key 一律沿用轉換值。所以沒調過的模型行為跟這個功能不存在時完全一樣。
* 手改檔案後不必重開遊戲 —— 目前沒有自動偵測,但在設定面板把模型切走再切回來會重建。
* 檔案內所有數值都**與縮放無關**(用倍率而非世界座標長度)。同一個模型套在高矮不同的舞者身上,unitScale 不一樣,但 physics.ini 還原出來的手感一致。
* 幾何**不**進這個檔:哪些骨頭是布料、碰撞形狀與碰撞群組、鏈長、是裙片還是髮束 —— 這些永遠從 .pmx 讀。檔案只帶「手感」。
* `DATA/MODEL` 整棵複製進打包版,所以 physics.ini 會跟著模型一起出貨。

分段是四個布料群組 `[bang] [hair] [skirt] [tie]` 加一個 `[global]`;群組是用剛體名稱關鍵字判的(見下),名稱對不上的模型全部落在 `[hair]`,那就在 `[hair]` 調。

## 物理的忠實度(現況)

轉換公式有用 pybullet 當真值校準過(`tools/mmd_cloth_validate/`,四個情境 rest/turn/walk/spin)。當時 22/38 PASS;之後為了跳舞好看把韌性調到 0.9,現在對真值是 **16/38** —— 裙子與雙馬尾刻意比 MMD 原生**硬**、擺幅小。這是手感取捨,不是壞掉。要往回校準就跑:

```
tools/mmd_cloth_validate/run_magica_probe.ps1     # 遊戲內錄 → magica_*.json
python tools/mmd_cloth_validate/compare.py        # 對真值 → report.md
```

⚠️ Magica Cloth 2 是付費資產(不入版控),而且**需要三個本地 patch**(重力上限、速度上限、MotionConstraint 距離上限),否則布料會變慢動作。重裝/升級 MC2 會默默弄壞物理 —— 見 `docs/MAGICA_LOCAL_PATCHES.md`。

## 相關檔案

| 檔案 | 作用 |
|---|---|
| `Assets/Scripts/Game/MmdModelCatalog.cs` | 掃 DATA/MODEL,一個資料夾一個模型(純邏輯 + 單元測試) |
| `Assets/Scripts/Game/MmdAvatarSwap.cs` | 讀 `config.ini [Mmd]`、模型選擇、解析快取、每隻角色的 SDO⇄MMD 切換(遠端角色各用各的模型) |
| `Assets/Scripts/Game/MmdModelStore.cs` | packId ⇄ 本機資料夾;下載區 `DATA/MODEL/.net/` |
| `Assets/Scripts/Sdo.Osu/ModelPackId.cs` | 模型的內容指紋(全檔 SHA-256)+ 整包驗證 |
| `Assets/Scripts/Sdo.Osu/ModelPackFilter.cs` | 哪些檔可以傳(client 與 server 編同一份) |
| `Assets/Scripts/UI/Core/NetModelTransfer.cs` | 上傳/下載的編排(歌優先、失敗不重試到底) |
| `server/Sdo.Server/Net/Hub.Blobs.cs` | server 收檔:依 kind 套白名單、驗上傳資格 |
| `Assets/Scripts/Sdo.Settings/StartupConfigSchema.cs` | 設定面板「MMD」分頁那幾列 |
| `Assets/Scripts/Game/PmxLoader.cs` | 執行期 .pmx 解析(頂點/材質/骨骼/剛體/關節) |
| `Assets/Scripts/Game/MmdAvatar.cs` | 建 rig、身高對齊、每幀從 SDO 骨架重定向 |
| `Assets/Scripts/Game/MmdBoneMap.cs` | MMD 日文骨名 → SDO Bip01 對應表 |
| `Assets/Scripts/Game/MmdMagicaCloth.cs` | PMX 剛體/關節 → Magica Cloth 2 |
| `Assets/Scripts/Game/MmdClothProfile.cs` | 每個模型的 physics.ini(有就用、沒有就轉換;逐 key 覆寫) |
| `tools/package_build.ps1` | `assets/MODEL` → `DATA/MODEL`(含 physics.ini) |
