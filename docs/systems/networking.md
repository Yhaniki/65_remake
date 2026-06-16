# 網路連線（Networking）

> Phase 1 **不做**。MVP 使用 **FishNet**。

## 職責

- 房間同步、輸入、結算廣播
- 斷線處理

## FishNet（MVP）

| 場景 | 行為 |
|------|------|
| 大廳/房間 | Server authoritative 或 host |
| 遊戲中 | 輸入 timestamp；判定可 host 或 server |
| 開局前 | 比對 `Chart.totalNotes` + hash |
| 斷線 | 標記離線；結算 0 分或最後分 |

## Phase 1

單機 offline。

## 診斷指標（Debug Overlay）

MVP 起 FishNet Transport 暴露給 [debug-overlay.md](debug-overlay.md)：

| 指標 | 計算 |
|------|------|
| **Ping** | RTT / 2（ms） |
| **RTT** | 最近 heartbeat 往返（ms） |
| **Jitter** | 最近 20 次 RTT 標準差 |
| **Loss %** | `(sent − acked) / sent` 最近 5s |
| **Tick** | Server `TimeManager.TickRate` |
| **Role** | Host / Client / Offline |

Phase 1 無連線；overlay 只顯示 FPS（見 debug-overlay）。

## 相關

- [room-matchmaking.md](room-matchmaking.md)
- [debug-overlay.md](debug-overlay.md)
- [architecture/online-services.md](../architecture/online-services.md)
