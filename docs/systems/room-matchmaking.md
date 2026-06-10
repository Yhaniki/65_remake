# 房間配對（Room Matchmaking）

> 優先級：**P0 MVP**
> 使用此 system 的 screen：[03-lobby](../screens/03-lobby/spec.md), [04-room](../screens/04-room/spec.md)
> 相關 flow：[03-room-lifecycle](../flows/03-room-lifecycle.md)

## 職責

- 房間建立 / 銷毀
- 玩家加入 / 離開
- 準備狀態同步
- 房主權限（選曲、開始、踢人）
- 房間列表廣播

## 原版行為（Reference）

- 大廳顯示公開房間列表
- 玩家建立房間成為房主
- 其他玩家加入，切換準備
- 房主選曲、按開始
- 房主可踢人

## MVP 規格

### 房間資料結構（草案）

```
Room {
  id: string
  hostId: string
  maxPlayers: number      // MVP 建議 6，待確認
  players: Player[]
  songId: string | null
  mode: string            // "free" | "normal"
  scrollDirection: string // MVP 固定 "up"
  status: "waiting" | "in_game"
  createdAt: timestamp
}

Player {
  userId: string
  displayName: string
  seatIndex: number
  isReady: boolean
  isHost: boolean
}
```

### 操作

| 操作 | 發起者 | 條件 |
|------|--------|------|
| 建立房間 | 任何玩家 | 不在其他房間內 |
| 加入房間 | 任何玩家 | 房間 waiting + 未滿 |
| 離開房間 | 任何玩家 | 在房間內 |
| 踢人 | 房主 | 目標非房主 |
| 準備/取消 | 任何玩家 | 在房間內 |
| 選曲 | 房主 | 房間 waiting |
| 開始 | 房主 | 全員 ready + 已選曲 |

### 同步

- 房間狀態變更 → 推送給所有房內玩家
- 大廳房間列表 → 定期刷新或事件推送

## 改版 / 優化

| 項目 | MVP | 之後 |
|------|-----|------|
| 房間密碼 | 不做 | P2 |
| 房主轉移 | 不做（房主離開=解散） | P1 |
| 快速加入 | 不做 | P1 |
| 觀戰 | 不做 | P2 |

## 待確認

- [ ] 原版房間 maxPlayers？
- [ ] 房主離開原版怎麼處理？
