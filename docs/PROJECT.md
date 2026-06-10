# 專案願景

## 一句話

用 **Unity** 重製 **熱舞 Online** 核心跳舞體驗；**Classic（原版向）** 與 **Enhanced（改良版）** 共用核心，改良版依附原版層擴充。玩法/skin/譜面基底參考 **[osu lazer](https://github.com/ppy/osu)**，SM 生態參考 **[SM-YHANIKI](https://github.com/Yhaniki/SM-YHANIKI)**。

## 雙變體

| 變體 | 定位 | 產物 |
|------|------|------|
| **Classic** | 盡量貼原版 SDO | 獨立 **Classic.exe** + Classic server |
| **Enhanced** | 現代改良；共用 Classic 函式庫 | 獨立 **Enhanced.exe** + Enhanced server |

**兩個程式、不混連** — 裝哪個 exe 進哪個大廳。詳見 [architecture/dual-variant.md](architecture/dual-variant.md)。

## 技術方向

| 項目 | 選型 |
|------|------|
| 引擎 | Unity |
| 連線 | FishNet |
| 發行/登入 | Facepunch Steamworks |
| 後端 | PlayFab |
| 譜面 runtime | Canonical Chart（SM / osu / GN import） |
| 計分 | Classic：SDO 原版；Enhanced：[hybrid 1e8](architecture/scoring-hybrid.md) |
| 產品變體 | 兩個獨立 exe（Classic / Enhanced），共用 core DLL — [dual-variant.md](architecture/dual-variant.md) |
| 多語系 | **文字 i18n**（非原版圖片語系）— [systems/localization.md](systems/localization.md)（對齊 osu LocalisableString + resx） |

見 [architecture/stack.md](architecture/stack.md)。

## 背景

- 原版：久游網，2007 年 Windows MMO 音遊
- 台灣：熱舞 Online；中國：超級舞者
- 考據：[reverse-engineering/](reverse-engineering/) · [reference/original-overview.md](reference/original-overview.md)

## 分階段目標

### Step 1（現在）

**1 首歌 · Tap · 純 `.osu` · 打判定**。見 [STEP1.md](STEP1.md)。

### Phase 1

+ 選歌 → Hold → 計分 → 結算。見 [PHASE1.md](PHASE1.md)。

### MVP

+ 登入（Steam）→ 大廳 → 房間 → 多人跳舞。見 [MVP.md](MVP.md)。

### 全案

+ 自由/普通模式、三方向 scroll、skin、VMD 舞蹈、SM/GN 譜、個人房（[咕盒 gogh](https://gogh.gg/zh-sc) 風）。

## 不做什麼（Phase 1）

- 登入、大廳、連線、Steam
- VMD、自訂 skin、家族/商場

## 成功標準

**Phase 1：** [STEP1.md](STEP1.md) Done → [PHASE1.md](PHASE1.md) Done。

**MVP：**

- [ ] Steam 登入 + 大廳房間
- [ ] 自由/普通模式（至少各 1 方向）
- [ ] 多人一局 + 結算
- [ ] totalNotes 對帳、1e8 計分

## 文件索引

| 想查… | 去哪 |
|-------|------|
| 第一階段 | `PHASE1.md` |
| 技術/計分/repo | `architecture/` |
| 畫面規格 | `screens/` |
| SDO 反編譯 | `reverse-engineering/` |
| 外部參考 | `reference/external-references.md` |
