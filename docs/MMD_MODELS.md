# 放 MMD 模型進遊戲 (DATA/MODEL)

## 怎麼加一個模型

一個資料夾 = 一個模型。把整包解壓進去、保持它原本的結構就好:

```
assets/MODEL/<模型名>/xxx.pmx        ← 開發樹 (編輯器直接讀)
assets/MODEL/<模型名>/textures/…       模型自己的 textures/Toon/Sph 原樣保留

<exe>/DATA/MODEL/<模型名>/xxx.pmx     ← 打包後的遊戲 (玩家自己加模型也是丟這裡)
```

`tools/package_build.ps1` 會把 `assets/MODEL` 整棵複製成 `DATA/MODEL`,所以**編輯器能跑的模型,打包出來的 dance.exe 也能跑**(在此之前打包版根本找不到模型,F7 按了沒反應)。

掃描規則在 `MmdModelCatalog.cs`(純邏輯,有單元測試):

* 「直接放著 .pmx 的資料夾」就是一個模型,資料夾名 = 顯示名稱。
* 往下找兩層,所以 `MODEL/某某整合包/miku/miku.pmx` 也掃得到。
* 有 .pmx 的資料夾就是葉子,不再往裡面找 → 模型自己的 `textures/`、`Toon/` 不會變成假模型。
* 一個資料夾裡有 `-JP`/`-EN`/`-CN` 多個 .pmx = 同一個模型的語系版本,**自動挑 JP**(理由見下)。
* 舊的單一模型路徑 `assets/IkaHatunemiku2025/` 仍然有效,不用搬。

## 遊戲裡怎麼用

| 鍵 | 作用 |
|---|---|
| F7 | SDO 原角色 ⇄ MMD 模型 |
| F9 | 換下一個模型 |
| F10 | 開/關 MMD 面板(◀ ▶ 選模型、⟳ 不重開遊戲重新掃資料夾、物理/著色微調) |

指令列:`dance.exe -mmd -mmdmodel <名稱>` 開場就進 MMD 模式並指定模型(名稱可以只給片段,例如 `-mmdmodel miku`)。

每個模型只解析一次並快取,所以 F9 來回切不會重複付解析成本。

## 換模型時什麼是自動的、什麼會出事

**自動(不用改任何程式碼)**

* **身高**:量模型自己的高度,縮放去對 SDO 骨架 —— 高矮胖瘦的模型都對得上。
* **物理**:頭髮/裙子/領帶讀**模型自己的剛體與關節**(碰撞形狀、碰撞群組遮罩、關節角度限制、阻尼)轉成 Magica Cloth 2。不是針對初音手寫的。模型沒帶剛體資料時,退回內建的身體碰撞體 + 彈簧骨。
* **貼圖/材質**:sphere(matcap)、toon、描邊都照 PMX 材質旗標走。

**會出事的三種模型**

1. **英文骨名**:重定向表 `MmdBoneMap.cs` 的 key 是 MMD 準標準**日文**骨名(センター/上半身/左腕/左ひざ…)。骨名不是這套 → 沒有骨頭被驅動 → 模型站著不動。同包有 JP 版就自動挑 JP,正是為此。
2. **剛體命名很特別**:`MmdMagicaCloth.GroupOf()` 用剛體名稱的關鍵字把布料分成 4 組(瀏海 `Bang|前髪` / 裙子 `Dress|Skirt|スカート|裙` / 領帶 `Tie|ネクタイ` / 其餘算頭髮)分別套手感參數。名稱對不上不會壞掉,只是全部落進「頭髮」那組,裙子可能偏硬或偏軟 —— 用面板的重力/硬度即時調。
3. **貼圖 V 翻轉**:少數模型 UV 是反的(領帶/腰帶最容易看出來),面板 `flipV` 按鈕切一下。

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
| `Assets/Scripts/Game/MmdDebug.cs` | F7/F9/F10、模型選擇、解析快取、面板 |
| `Assets/Scripts/Game/PmxLoader.cs` | 執行期 .pmx 解析(頂點/材質/骨骼/剛體/關節) |
| `Assets/Scripts/Game/MmdAvatar.cs` | 建 rig、身高對齊、每幀從 SDO 骨架重定向 |
| `Assets/Scripts/Game/MmdBoneMap.cs` | MMD 日文骨名 → SDO Bip01 對應表 |
| `Assets/Scripts/Game/MmdMagicaCloth.cs` | PMX 剛體/關節 → Magica Cloth 2 |
| `tools/package_build.ps1` | `assets/MODEL` → `DATA/MODEL` |
