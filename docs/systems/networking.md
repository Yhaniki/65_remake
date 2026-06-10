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

## 相關

- [room-matchmaking.md](room-matchmaking.md)
- [architecture/online-services.md](../architecture/online-services.md)
