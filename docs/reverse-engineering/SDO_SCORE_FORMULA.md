# SDO 單機版算分公式說明

本文依 `tools/sdo_stand_alone.exe.c`（Ghidra 反編譯）與 Dance! Online 伺服器逆向註解（`thirdparty/net.arrowgene.dance/.../GameStatsData.java`）整理，說明 **Perfect / Cool / Bad / Miss** 判定與分數、Combo 的關係。

---

## 1. 判定種類（Panjung）

遊戲內部以**整數代碼**表示判定，主要處理函式為 `FUN_00497500`（音符命中後更新統計與特效）。

| 顯示名稱 | 內部代碼 | 計數器偏移（相對 gameplay 物件） | 說明 |
|---------|---------|----------------------------------|------|
| **Perfect** | `4` | `+0x10964` | 最佳判定 |
| **Cool** | `2` | `+0x1096c` | 次佳 |
| **Bad** | `3` | `+0x10968` | 偏差較大仍算過關 |
| **Miss** | `0` 或 `1` | `+0x10970` | 未命中或過期 |

判定結果由 `FUN_0048c4a0` 依**時間誤差**（當前時間與音符位置的差，取絕對值）計算，再經 `FUN_004978c0` → `FUN_00497500` 套用。

### 1.1 一般模式時間窗（`FUN_0048c4a0`，約略單位：毫秒級 tick）

常數（`param_7 == 0` 時寫入）：

| 符號 | 值 | 用途 |
|------|-----|------|
| `W_max` | 25 (`0x19`) | 可判定最大誤差 |
| `W_cool` | 20 (`0x14`) | Cool 外界 |
| `W_bad` | 15 (`0x0f`) | Bad 外界 |
| `W_inner` | 6 (`0x06`) | 內圈門檻 |

在一般旗標下（`+0x109b0 == 0`），誤差 `e` 與判定對應為：

| 誤差 `e` | 判定代碼 | 顯示 |
|----------|----------|------|
| `e ≤ 6` | 4 | Perfect |
| `7 ≤ e ≤ 15` | 3 | Bad |
| `16 ≤ e ≤ 20` | 2 | Cool |
| `21 ≤ e ≤ 25` | 1 | Miss（視為未命中） |
| `e > 25` | 0 | Miss |

> 練習／特殊模式（`DAT_00674f04 + 0x74` 等）會改用較窄的 `W_max=20, W_cool=16, W_bad=10, W_inner=5`，邏輯相同但數值更小。

---

## 2. Combo（連擊）

| 項目 | 偏移 / 行為 |
|------|-------------|
| 當前 Combo | `+0x10960`，Perfect / Bad 連續命中時遞增 |
| Combo 中斷 | Cool、Miss 時將 `+0x10974`、`+0x10960` 清零 |
| 連擊里程碑 | `+0x10974` 連續 Perfect/Bad 次數 > 1 時觸發 `FUN_0049d4a0` 顯示 Combo 數字（寫入 `+0x3110`） |
| 實際加分用 Combo | 每顆音符當下的 Combo，見下文公式中的 `(combo + 1)` |

**重要：** Combo 越高，**每一顆** Perfect 的加分越多（見第 3 節）。

---

## 3. 單顆音符得分（核心公式）

`GameStatsData.java` 中保留了客戶端 Lua 腳本的原始註解，與 SDO 系譜一致，**每顆音符**得分為：

```text
curScore = cLevelGajoong[level] × cLevelScore[level] × cGajoong[judge] × (combo + 1) / 2.0
```

### 3.1 判定倍率 `cGajoong`（Lua 1-based 索引）

```lua
cGajoong = { 2.0, 1.5, 1.0, 0.7, 0.0 }
-- 對應 m_currPanjung + 1，即 Perfect 最高、Miss 為 0
```

| 判定 | 倍率 | 相對 Perfect |
|------|------|----------------|
| Perfect | **2.0** | 100% |
| Cool | **1.5** | 75% |
| Bad | **1.0** | 50% |
| （次級） | **0.7** | 35% |
| Miss | **0.0** | 0 |

同一 Combo、同一關卡係數下，**Cool 單顆約為 Perfect 的 75%**，差額為 Perfect 的 **25%**。

### 3.2 關卡／難度基礎分 `cLevelScore`（等級 0–11）

```lua
cLevelScore = { 0, 600, 750, 900, 1050, 1200, 1350, 1600, 1850, 2100, 2350, 2600 }
```

- 索引由玩家／歌曲難度等級決定（客戶端 `FUN_0048c660` 依 `+0x68` 浮點倍率區間對應 0–8 級，再對應上表）。
- 等級 0 時基礎分為 0（教學或特殊段）。

### 3.3 等級額外倍率 `cLevelGajoong`

```lua
cLevelGajoong = { 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2 }
```

- 等級 **1–6**：倍率 **1**
- 等級 **7–12**：倍率 **2**（高難度關卡總分約翻倍）

### 3.4 Combo 倍率

```text
(combo + 1) / 2.0
```

| 當前 Combo（命中前） | 倍率 |
|---------------------|------|
| 0 | 0.5 |
| 1 | 1.0 |
| 9 | 5.0 |
| 19 | 10.0 |
| 67 | 34.0 |

Combo 從 0 開始，**第一顆 Perfect 只有一半 Combo 加成**；連擊越高，每顆分數線性增加。

---

## 4. 整局總分（簡化式，與「每顆 Cool 少 10 分」）

實戰中若用**整局最大 Combo** `C` 近似每顆音符的 Combo 倍率（見 `GameStatsData.getPoints()`），可得：

```text
C = clamp(maxCombo, 10, 68)

總分 = perfects × C + cools × (C - 10)
```

### 4.1 為何「多 1 個 Cool 就少 10 分」？

在 `C` 不變、其餘皆 Perfect 時：

```text
全 Perfect：N × C
少 1 Perfect、多 1 Cool：(N-1)×C + (C-10) = N×C - 10
```

因此 **每多 1 個 Cool（少 1 個 Perfect），總分差 10 分**——與實測一致。

> 此簡化式在 **maxCombo ≥ 10** 且用最大 Combo 代表全程時成立；若全程 Combo 很低，應改用第 3 節逐顆累加。

### 4.2 範例（`C = 68`）

| 情況 | 計算 | 總分 |
|------|------|------|
| 82 Perfect，0 Cool | `82 × 68` | **5576** |
| 81 Perfect，1 Cool | `81×68 + 1×58` | **5566**（差 10） |
| 40 Perfect，4 鍵段 | `82 × 68`（註解中的 5576 = Perfect × combo） | 見 Java 註解實測 |

`GameStatsData.java` 註解中的 packet 實測（`5576 = 82 × 68`）與上式一致。

---

## 5. 與反編譯 C 碼的對應

| 功能 | 函式 | 說明 |
|------|------|------|
| 時間 → 判定代碼 | `FUN_0048c4a0` | 設定 Perfect/Cool/Bad/Miss 窗口 |
| 命中處理 | `FUN_004978c0` | 計算誤差、呼叫判定 |
| 統計 + Combo | `FUN_00497500` | 更新 perfect/cool/bad/miss 計數、Combo |
| 結算分數顯示 | `+0x4604` 等 | 傳至結算 UI（`hitint` / `hitdeci` / `score` 控件） |
| 結果畫面字串 | `perfect%d`, `cool%d`, `bad%d`, `miss%d`, `combo%d`, `score%d` | 約 `FUN_00488xxx` 一帶 UI 初始化 |

Lua 常數表（600、750… 與 2.0、1.5…）可能位於腳本資源或 DLL，**.c 檔中未以明文陣列出現**；上表公式以 Arrowgene 專案註解與你提供的 **Cool 差 10 分** 實測交叉驗證。

---

## 6. 其他相關倍率（非算分主公式）

`FUN_0048c5a0` 依歌曲／模式設定 `+0x68` 浮點難度倍率，例如：

| case | 浮點值 | 約略 |
|------|--------|------|
| 0 | `0x3f800000` | 1.0 |
| 1 | `0x3fc00000` | 1.5 |
| 2 | `0x40000000` | 2.0 |
| … | … | 最高約 8.0 |

這會影響 `cLevelScore` 索引或顯示難度，與第 3.2 節等級表連動。

---

## 7. 小結公式卡

**逐顆（精確）：**

```text
得分 = LevelG × LevelScore[level] × JudgeMul[judge] × (combo + 1) / 2
```

**整局近似（maxCombo ≥ 10）：**

```text
總分 = P × clamp(maxCombo,10,68) + C × (clamp(maxCombo,10,68) - 10)
```

其中 `P` = Perfect 數，`C` = Cool 數；**每個 Cool 相對 Perfect 固定少 10 分**（在簡化式下）。

---

## 8. 參考檔案

- `tools/sdo_stand_alone.exe.c` — `FUN_0048c4a0`, `FUN_00497500`, `FUN_004978c0`
- `thirdparty/net.arrowgene.dance-.../server/.../GameStatsData.java` — Lua 原始公式與 `getPoints()` 簡化實作

---

*若需對某一關卡等級逐顆驗證分數，請提供：關卡等級、`maxCombo`、各判定數量，可依第 3 節或第 4 節代回計算對照。*
