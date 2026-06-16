# Debug Overlay（效能／網路除錯疊層）

> **Enhanced only**（Classic exe 不提供 UI 開關；QA 可用啟動參數強開）。  
> 對標 osu lazer `FrameStatistics`、Steam/FishNet 連線診斷；**不進結算畫面**（見 [result-screen.md](../screens/05-game-arena/result-screen.md) 取捨）。

---

## 目的

開發／測試時在畫面角落疊一層**可開關**技術資訊，不用離開遊戲就能看：

- 本機 **FPS**、幀時間
- 連線時 **延遲、抖動、封包遺失** 等網路健康度

正式玩家預設關閉；開了也不影響判定與計分。

---

## 顯示位置與樣式

| 項目 | 規格 |
|------|------|
| 位置 | 左上角（`padding 8px`）；不擋 playfield 中央判定線 |
| 層級 | 最上層 UI Canvas；`sortingOrder` 高於 HUD、低於 modal |
| 背景 | 半透明黑底 `rgba(0,0,0,0.55)`；等寬小字 |
| 更新 | FPS／幀時間每幀；網路欄位 **0.5s** 滑動平均 |
| 錄影／截圖 | 跟隨開關；關閉時不出現在截圖（與 HUD 分層） |

### 範例（MVP 連線中）

```
FPS 144.2  (6.9 ms)
Ping 38 ms  Jitter 4 ms
Loss 0.0%   RTT 76 ms
Tick 20 Hz  Role Client
```

---

## 欄位（依階段）

### Phase 1（單機）

| 欄位 | 說明 |
|------|------|
| **FPS** | `1 / unscaledDeltaTime` 滑動平均 |
| **Frame ms** | CPU 主線程幀時間（`Time.deltaTime × 1000`） |
| **Mem MB**（可選） | `GC.GetTotalMemory` 約略；P1 再開 |

### MVP+（FishNet 連線）

在 Phase 1 基礎上加：

| 欄位 | 來源 | 說明 |
|------|------|------|
| **Ping** | FishNet RTT 單向估算 | 毫秒；見 [networking.md](networking.md) |
| **Jitter** | 最近 N 次 RTT 標準差 | 毫秒 |
| **Loss %** | 送／收封包序號缺口 | 最近 5s 視窗 |
| **RTT** | 往返延遲 | 毫秒 |
| **Tick** | Server `TickRate` | Hz |
| **Role** | `Host` / `Client` / `Offline` | 連線角色 |
| **Interp ms**（P1） | 輸入 timestamp 與 server 時鐘差 | 判定延遲調參用 |

斷線／單機時網路欄位顯示 `—` 或隱藏該段。

---

## 開關方式

| 方式 | 階段 | 行為 |
|------|------|------|
| **熱鍵 `F12`** | Phase 1+ | 全域切換；大廳／房間／遊玩皆可 |
| **OPTION → 游戏** | MVP | 勾選「顯示除錯資訊」；與熱鍵共用同一 bool |
| **啟動參數 `-debugoverlay`** | 全階段 | 強制開啟（CI／QA）；忽略存檔 |
| **啟動參數 `-nodebugoverlay`** | Release | 強制關閉（覆蓋存檔與熱鍵） |

預設：**關**。

---

## 設定儲存

寫入本地 `settings.json`，**不上傳 Steam Cloud**（與 [game-settings.md](game-settings.md#設定儲存與同步)「除錯 flag 不同步」一致）。

```json
{
  "show_debug_overlay": false
}
```

---

## 實作切塊

```
Remake.Unity.Enhanced/
└── Assets/Scripts/
    └── Debug/
        ├── DebugOverlayView.cs      # UI 綁定
        ├── DebugOverlayController.cs # 開關、熱鍵、啟動參數
        ├── FrameStatsSampler.cs     # FPS / frame ms
        └── NetworkStatsSampler.cs   # MVP+；讀 FishNet Transport 統計
```

- `FrameStatsSampler`：Step 1 即可 stub，Phase 1 接 FPS。
- `NetworkStatsSampler`：MVP 接 FishNet；Phase 1 不編譯或回傳空。

Prefab：`DebugOverlayCanvas` 掛在 `DontDestroyOnLoad` bootstrap；預設 `SetActive(false)`。

---

## 階段

| 階段 | 範圍 |
|------|------|
| Step 1 | 可選：Editor 內建 Stats；不強制玩家 UI |
| **Phase 1** | FPS + Frame ms；`F12` 開關 |
| **MVP** | + 網路欄位；OPTION 勾選 |
| Release | 預設關；`-nodebugoverlay` 可完全禁用 |

---

## 不做

- 結算表顯示 FPS（干擾玩家）
- 把 overlay 寫進 replay `.rpl`
- Classic OPTION 常駐開關（僅 QA 啟動參數）

---

## 相關

- [game-settings.md](game-settings.md) — OPTION 開關入口
- [networking.md](networking.md) — RTT／loss 資料來源
- [screens/05-game-arena/spec.md](../screens/05-game-arena/spec.md) — 局內 HUD 共存
