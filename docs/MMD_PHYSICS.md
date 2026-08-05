# MMD 頭髮/裙擺物理 —— 現況、問題、待辦

> 最後更新:2026-08-04。涵蓋 `main` 上的 Magica 路線與 worktree `H:/65_remake-mmdphys`
> (branch `feat/mmd-bullet-physics`) 上的 Bullet 原生路線。

## 一句話現況

**現在遊戲跑的是「把 MMD 的物理資料轉換成 Magica Cloth 參數」,那個轉換做不到對、只能接近;
新開的另一條路是「直接跑 MMD 自己的 Bullet 剛體物理」,數值上驗證是對的,但目前太慢不能用。**

---

## 目標

讓 MMD 模型的頭髮/裙擺/領帶,在遊戲裡的行為**跟在 MMD 裡一樣**。

判準不是「看起來順眼」,而是可量測的:同一個模型、同一段動作,跟 MMD 底層(Bullet)算出來的結果比,
每根骨頭的位置差多少。

---

## 兩條路

| | Magica 轉換(現行,`main`) | Bullet 原生(`feat/mmd-bullet-physics`) |
|---|---|---|
| 做法 | 讀 .pmx 的剛體/關節,**換算**成布料參數 | 直接把 .pmx 的數字當 Bullet 的數字跑 |
| 要調參數嗎 | 要,而且很多旋鈕在 .pmx 裡**沒有對應的東西** | 完全不用 |
| 效能 | 可用(Magica 是 DOTS/Burst) | **不可用**(8.5 ms/幀 @ .NET;Mono 還要再乘 2~3,且每隻角色一份) |
| 正確性 | 結構上做不到(見下) | 建構與第一幀已驗證吻合;長鏈尖端仍有偏差 |
| 開關 | 預設 | 設定面板 MMD 分頁「物理求解器」→ `bullet`(`config.ini mmdPhysicsEngine`) |

---

## 已經查清楚的事(都有數據)

### 1. per-part 平均是一個實質 bug(已修,`77ac795`)

轉換原本每個硬編部位(Bang/Hair/Skirt/Tie)算一組參數,值是**整群平均**。初音的 hair 群 =
446 骨 / 24 條鏈:10 條 30 節雙馬尾(關節鎖死、無彈簧)混著 26 根 3 節短髮(彈簧 5.0)。
平均後 `springMean=0.291` 落進「有彈簧」分支 → 整群 `angleStiffness 0.052`,**佔 94% 的
雙馬尾拿到全模型最軟的值**,而使用者手調的 0.9 只落在其它三群。

修法:`MmdClothChains.cs` —— 先切成鏈,每條用自己的關節資料算,只把「行為簽章相同」的鏈併成
一個 cloth。雙馬尾靜止垂角 8.4° → 31.1°(真值 31.8°)。

### 2. PBD 模仿 Bullet 有結構性的天花板

| MMD(Bullet) | Magica(PBD) | 對應到的症狀 |
|---|---|---|
| 關節限制只在邊界擋一下 | 限制也是「把點硬搬回去」,每步都搬 | 起始速度爆衝、Q 彈 |
| 有質量差、力矩沿鏈傳遞 | 點沒有質量 | 甩幅只能靠 `depthInertia` 硬湊,0/0.47/1 都偏 |
| 線性 + 角阻尼兩套 | 只有一個速度衰減 | 領帶調對了裙子就過鈍 |
| 衝量法,靜止會收斂 | 位置投影會**注入能量** | 靜止 4 秒頭髮還在晃 |

量測(靜止情境,雙馬尾尖端速度):

| | 起始 | 2 秒後 | 4 秒後 |
|---|---|---|---|
| MMD/Bullet | 0.99 m/s | 0.05 | 0.002(停了) |
| Magica(per-chain 版) | 2.24 | 1.23 | 0.087(還在晃) |

**這就是「Q 彈」**,而且不是哪個數字設錯 —— 分數一直卡在 20~21/38,改任何旋鈕都是「這條 PASS
那條 FAIL」,那是兩個系統不同構的典型徵狀。

### 3. ⚠️ 那份「地面真值」的碰撞過濾是錯的

`tools/mmd_cloth_validate/mmd_ref_sim.py`(pybullet 重建)一直被當成真值,但它**沒有套用作者的
碰撞群組**:雙馬尾根部依 .pmx 一個合法碰撞對象都沒有(連它的關節父體「頭」都是 filter=False),
pybullet 卻回報 10 個接觸(跟頭、跟 HairLine),全世界 1090 個。

三個對照實驗釘死來源:

| 參考版本 | 根部第一幀位移 |
|---|---|
| 原版 / 接觸列加過濾 / **完全移除接觸列** | 全都是 0.0832 |
| group-mask 全設 0 | 0.0014 |

→ 推力來自 pybullet 內部,而 MMD 本體與 three.js 用的是 Bullet 預設的雙向 AND 過濾。

**影響**:先前 Magica 那條路的**每一輪校準,都是在對齊一份「頭髮會互撞、頭髮會撞頭」的參考**。
雙馬尾在真值裡沒有完全垂下來,可能有一部分就是被這些不該存在的接觸撐住的。

### 4. 驗證管線只能走 player

MC2 在 Unity Test Framework(`-batchmode -runTests`)底下**完全不 step** —— 連原廠 BoneCloth
都不動,錄出來每條鏈都是完美剛性。一律用 `tools/mmd_cloth_validate/run_magica_probe.ps1`
(build player → `dance.exe -mmdprobe`)。`compare.py` 開頭的 DATA VALIDITY 就是在擋假資料。

### 5. Bullet 移植:哪些已經驗證吻合

| 檢查 | 結果 |
|---|---|
| 第 0 幀(只反映建構:rest 姿勢/骨偏移/euler 慣例) | 誤差 **0.000007** |
| 第 1 幀位移比(瀏海 3 節 / 領帶 20 節) | **1.10x / 1.00x** |
| 慣性張量 vs pybullet 逐項 | **1.000**(含膠囊真的用外接盒) |
| 4 秒後誤差(對**無碰撞**參考) | 根部 0.014、瀏海 0.10、長鏈尖端 1.0~1.8 模型單位 |

---

## 現在的問題

### Magica 路線(現行)
1. **Q 彈**:靜止時停不下來(見上表)。PBD 的結構性問題,調參數解不掉。
2. **進場才垂落**:應該一進場就是垂好的。MC2 沒有「快轉 N 步」的 API(`SetTimeScale` 被
   `Clamp01`),要嘛在遮罩後面先跑完,要嘛預先算好平衡姿勢。
3. **校準基準有問題**:見上面第 3 點。用一份修好的參考重新校準,可能不用改架構就有改善。
4. 裙子 2/10 是刻意的取捨(AutomaticMesh + maxDistance 拴繩換防穿模)。

### Bullet 路線(新)
1. **太慢**:8.5 ms/幀(723 剛體、double、單執行緒、.NET Release)。Unity Mono 再乘 2~3,
   而且每隻角色一份。**這是它能不能上線的分水嶺。**
2. **效果評價還不準**:卡的時候固定 1/60 步進追不上,布料變慢動作、驅動骨速度失真 ——
   在跑順之前看到的效果不能當作物理本身的評價。
3. **長鏈尖端仍偏**(雙馬尾 mean ~1.0 模型單位 ≈ 8 cm),鏈長比參考短 2.6~5%。方向是
   「參考端的關節被重力拉出 sag、我們的比較硬」。已排除:建構、第一幀動力學、慣性張量。

---

## TODO(按優先)

### A. 讓 Bullet 路線能用
- [ ] **Burst/Jobs 化**(分水嶺):求解器改成 struct + NativeArray。演算法已驗證,是機械性移植。
- [ ] 效能還不夠的話:每隻角色的 LOD(遠處/頭貼降迭代或關掉)、只有本機玩家用 bullet。
- [ ] 長鏈 sag 的殘餘偏差(要先有可信的參考,見 B)。

### B. 修驗證基準
- [ ] 讓參考端真的套用作者的碰撞過濾。`setCollisionFilterGroupMask` 對這些 multibody base 沒用,
      要嘛逼 pybullet 套用,要嘛參考端的接觸也自己算(那樣兩邊就都照作者的設定)。
- [ ] 用修好的參考重跑,重新看 Magica 那 38 個指標 —— 有可能現有手感的一部分「偏差」其實是
      在追一個錯的目標。

### C. Magica 路線(在 Bullet 上線前它還是主力)
- [ ] 進場預垂落:在 loading/轉場遮罩後面就把布料跑完,或預先算平衡姿勢當初始。
- [ ] 用修好的基準重新校準(成本比 Burst 化低得多)。

### D. 其它
- [ ] `dance` 情境目前只有 magica 端的絕對穿模數字,沒有 Bullet 真值可比(要在參考端做 per-bone FK)。
- [ ] 每個模型的 `physics.ini`(存檔/還原按鈕已經有了)只影響 Magica 路線;Bullet 路線沒有可調參數。

---

## 檔案地圖

### `main`
```
65/My project/Assets/Scripts/Game/
  PmxLoader.cs            .pmx 解析(含完整 6DOF 關節)
  MmdClothChains.cs       ★ 切鏈 + 每條鏈自己算 + 行為簽章合併(純邏輯,18 個單元測試)
  MmdMagicaCloth.cs       把上面的結果做成 Magica Cloth 元件
  MmdClothProfile.cs      每個模型的 physics.ini(有檔用檔,沒檔用轉換)
  MmdAvatar.cs            MMD 模型顯示 + retarget(物理骨會被跳過,不跟布料搶)
  MmdPhysicsProbe.cs      遊戲內探針(-mmdprobe):rest/turn/walk/spin/dance + 穿模量表
tools/mmd_cloth_validate/
  mmd_ref_sim.py          pybullet 重建的參考(⚠ 碰撞過濾有問題)
  run_magica_probe.ps1    build player → 跑探針 → 收檔 → 算指標
  compare.py              比對 + report.md(含 DATA VALIDITY 守門、靜態/動態分組、穿模欄)
docs/MAGICA_LOCAL_PATCHES.md   MC2 的三個本地 patch(重裝必重套)
```

### `feat/mmd-bullet-physics`(worktree `H:/65_remake-mmdphys`)
```
65/My project/Assets/Scripts/Sdo.MmdPhysics/     ← 不依賴 UnityEngine(asmdef noEngineReferences)
  MmdMath.cs              double 精度 V3/M3/Q
  MmdRigidWorld.cs        ★ 剛體 + 6DOF 約束求解(ERP 0.2、10 迭代、warm start、邊著色)
  MmdCollision.cs         球/盒/膠囊接觸 + 作者的 group/mask
65/My project/Assets/Scripts/Game/MmdBulletCloth.cs   接進遊戲(驅動 kinematic → 步進 → 寫回骨頭)
tools/mmd_bullet_port/
  export_physics.py       把 .pmx 的物理段匯出成 JSON
  Program.cs + .csproj    ★ 編**同一份**求解器原始碼,對 ref_*.json 逐幀比對 —— 不用 Unity
```

> ⚠ 這個 worktree 開 Unity 前要先把 MagicaCloth2 接過去(它是付費資產、在 .gitignore):
> `New-Item -ItemType Junction -Path "<worktree>\65\My project\Assets\MagicaCloth2" -Target "H:\65_remake\65\My project\Assets\MagicaCloth2"`
> 再把 `MagicaCloth2.meta` 複製過去,否則一開就是 Safe Mode。

---

## 怎麼跑

```powershell
# Magica 路線:build player → 錄 5 個情境 → 算指標 → 比對
./tools/mmd_cloth_validate/run_magica_probe.ps1      # 已經有 exe 就加 -SkipBuild
python tools/mmd_cloth_validate/compare.py           # → report.md

# Bullet 路線:秒級迭代,不用開 Unity
cd tools/mmd_bullet_port
python export_physics.py                             # 只要模型換了才需要重跑
dotnet run -c Release -- rest 4                      # [情境] [秒數] [參考後綴:nocol/filtered]
```

基準(2026-08-04,player 探針,初音):Magica 路線 **20 PASS / 18 FAIL** ——
瀏海 9/10、雙馬尾 6/10(per-chain 之前是 3/10)、領帶 4/9、裙子 2/10(刻意)。
穿模欄:靜態情境 0.00cm / 0 幀、舞蹈情境 0.16cm / 28 幀。

---

## 三模型自動轉換 corpus（2026-08-05）

這套 corpus 是用來驗證「任意 PMX 先經過同一套自動分析與轉換」，不是為三個模型各寫一份硬編參數。固定 fixture 記錄在
`tools/mmd_cloth_validate/model_corpus.json`，每個 fixture 都指定確定的 PMX 與 SHA-256，避免模型包內有多個角色或材質變體時誤測到另一個檔案。

| ID | fixture | 預設位置 |
|---|---|---|
| `ika_miku_2025` | Ika Hatsune Miku 2025 | repo 內的 `assets/MODEL/IkaHatunemiku2025` |
| `yyb_miku_10th` | YYB Hatsune Miku 10th v1.02 | `H:/sdo/sdo_mot/bak/YYB Hatsune Miku_10th` |
| `izayoi_sakuya_220_rq` | 十六夜咲夜 Ver2.20 RQ Type-A | `H:/sdo/sdo_mot/bak/十六夜咲夜Ver2.20_RQスタイル` |

在 repo 根目錄重跑：

```powershell
python tools/mmd_cloth_validate/mmd_corpus.py
```

報告會寫到：

- `test-output/mmd-physics-corpus/structural-report.json`
- `test-output/mmd-physics-corpus/structural-report.md`

外部 fixture 搬家後不用改 manifest；以環境變數覆寫根目錄即可：

```powershell
$env:MMD_FIXTURE_YYB_10TH = 'D:/MMD/YYB Hatsune Miku_10th'
$env:MMD_FIXTURE_SAKUYA_220_RQ = 'D:/MMD/十六夜咲夜Ver2.20_RQスタイル'
python tools/mmd_cloth_validate/mmd_corpus.py
```

也可只檢查單一模型，或做單次根目錄覆寫：

```powershell
python tools/mmd_cloth_validate/mmd_corpus.py --model yyb_miku_10th
python tools/mmd_cloth_validate/mmd_corpus.py --root 'yyb_miku_10th=D:/MMD/YYB Hatsune Miku_10th'
```

目前三個 fixture 的結構覆蓋結果：

| ID | 可對應動態剛體 | 自動分出的 chain roots | 結構等級 |
|---|---:|---:|---|
| `ika_miku_2025` | 650 / 650 | 47 | approximate |
| `yyb_miku_10th` | 275 / 275 | 32 | approximate |
| `izayoi_sakuya_220_rq` | 39 / 39 | 20 | approximate |

**100% 結構對應不等於 Bullet 動作等價。** 這只證明現行轉換器可以將每個動態剛體對應到布料骨，並建立可交給 Magica Cloth 2 的 chain。它沒有證明限位、回彈、碰撞、阻尼或動作軌跡與 MMD/Bullet 一致。三個模型仍有以下風險：

- YYB 與咲夜含 mode 2 剛體，目前只能近似。
- PMX 6DOF joint 的線性位移限位/彈簧沒有被 BoneCloth 完整保留。
- 作者的 rigidbody joint graph 可能分支、成環，或不同於 bone parent hierarchy；現行轉換會將它簡化成 bone chains。
- 動態剛體之間的形狀、group/mask 與剛體碰撞並未原樣保留；YYB 與咲夜的 kinematic box 也會被近似。

因此 structural corpus 是「自動轉換的入口與結構覆蓋」回歸測試；下面的 player probe 則證明同一套 runtime 轉換在三個 fixture 上能完成基本模擬。

### 三模型 player probe

`run_magica_probe.ps1` 可以用 `-ModelPath` 指定任意 PMX、`-ModelId` 標記 fixture，並用 `-OutputDir` 將每個模型的結果隔離在目前 worktree 內。第一個模型 build player，其餘模型加 `-SkipBuild` 重用同一份 `dance.exe`：

```powershell
./tools/mmd_cloth_validate/run_magica_probe.ps1 `
  -ModelPath 'assets/MODEL/IkaHatunemiku2025/Ika-HatsuneMiku 2025-JP.Pmx' `
  -ModelId ika_miku_2025 `
  -OutputDir 'test-output/mmd-physics-corpus/player/ika_miku_2025'

# YYB 與咲夜使用 manifest 中的 PMX/ID，並加 -SkipBuild。
```

每次 probe 都有嚴格 gate，任一項失敗就不接受該輪結果：

- 輸出必須剛好包含 `rest`、`turn`、`walk`、`spin`、`dance` 五個情境，不可缺檔或混入前一輪的額外情境。
- `model.json` 記錄的 SHA-256 必須與 `-ModelPath` 的實際 PMX 一致。
- runtime 必須驗證所有 physics bone 每個採樣都是 finite；任何 NaN/Infinity 都失敗。
- 四條 trace chain 不能全部剛性不動（相對形變門檻 1 mm），否則視為 MC2 沒有 step。

最終結果來自同一份 player build；「接受幀數」是五個情境合計，`warmup dropped` 是 finite gate 允許在正式錄製前丟棄的暖機幀：

| ID | bones / rigid / dynamic / structural roots | trace chains | 接受幀數 | warmup dropped | 最大形變 | 最大穿模深度 |
|---|---:|---:|---:|---:|---:|---:|
| `ika_miku_2025` | 815 / 723 / 650 / 47 | 4 | 4,293 | 1 | 1.0627 m | 0.0016 m |
| `yyb_miku_10th` | 439 / 298 / 275 / 32 | 4 | 10,743 | 0 | 0.6486 m | 0.0656 m |
| `izayoi_sakuya_220_rq` | 171 / 58 / 39 / 20 | 4 | 23,934 | 0 | 0.0271 m | 0 m |

原始錄製在 `test-output/mmd-physics-corpus/player/<id>/magica_<scenario>.json`，模型身分在同目錄的 `model.json`，嚴格 gate 摘要在 `probe-validation.json`。結構報告仍在 `test-output/mmd-physics-corpus/structural-report.json` 與 `structural-report.md`。

這證明 **3/3 player smoke stability**：三個模型都能載入、建立 MC2 cloth、完成五情境並產生非剛性軌跡。它仍然 **不是 MMD/Bullet 動作等價證明**；最大形變是 cloth liveness 證據，不代表動作正確。YYB 的穿模是真實轉換缺口：`dance` 有 520 / 2,689 幀命中，`turn` 有 4 / 1,777 幀命中，而非可以忽略的 probe 雜訊。mode 2、線性 joint、rigidbody graph 與碰撞簡化等結構風險也仍未解決；要判斷 Unity 中是否接近 MMD，仍需以相同動作、fixed timestep 與可信的 Bullet 參考軌跡比較。
