# Flow：首次使用者

> 優先級：P1（MVP 可簡化為直接登入，不做新手教學）

## 目的

描述第一次開啟遊戲的玩家，從看到登入畫面到完成第一局跳舞的完整路徑。

## 流程步驟

```
1. 啟動遊戲
2. 看到登入介面
3. 註冊新帳號（或訪客試玩 — 待確認）
4. 登入成功
5. 選擇伺服器
6. 進入大廳（可能看到新手提示 — post-MVP）
7. 建立或加入房間
8. 在房間內準備
9. 房主選曲並開始
10. 完成第一局遊戲
11. 看到結算
12. 回到房間
```

## 各步驟對應 Screen

| 步驟 | Screen | 文件 |
|------|--------|------|
| 2-4 | 登入 | [01-login/spec.md](../screens/01-login/spec.md) |
| 5 | 伺服器選擇 | [02-server-select/spec.md](../screens/02-server-select/spec.md) |
| 6 | 大廳 | [03-lobby/spec.md](../screens/03-lobby/spec.md) |
| 7-8 | 房間 | [04-room/spec.md](../screens/04-room/spec.md) |
| 9-11 | 遊戲場地 | [05-game-arena/spec.md](../screens/05-game-arena/spec.md) |

## MVP 簡化

- 不做新手教學 overlay
- 不做強制註冊引導（若已有帳號直接登入）
- 預設外觀，不進商場

## 待確認

- [ ] 原版有沒有新手教程？
- [ ] 第一次登入有沒有送道具/服裝？
