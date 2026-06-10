# 遊戲場地

> 優先級：**P0 MVP**
> 相關 flow：[docs/flows/02-login-to-game.md](../../flows/02-login-to-game.md)
> 相關 system：[docs/systems/scoring-judgment.md](../../systems/scoring-judgment.md), [docs/systems/audio-bgm.md](../../systems/audio-bgm.md)

## 1. 目的

玩家跟著音樂節奏按鍵，完成舞蹈判定，一局結束後結算並回房間。

- **進入條件**：房間全員準備，房主按開始
- **成功離開**：歌曲結束 → 結算 → 回 `04-room`

## 2. 原版行為（Reference）

- 3D 場景，多個玩家角色同時跳舞
- 音符從畫面下方（向上模式）飛入 **判定線**（Enhanced：**playfield 中央**，osu mania 式 — [game-settings.md](../../systems/game-settings.md#判定線hit-line)）
- 玩家按對應方向鍵（↑↓←→）命中音符
- 判定等級：**Perfect / Cool / Bad / Miss**（四級，見 [result-screen.md](./result-screen.md) 考據）
- 連擊 Combo 計數
- 即時顯示各玩家分數
- 歌曲結束 → **結算畫面**（全員成績表、G 幣/经验奖励、确定回房）
- 結算後回房間

> 結算詳見 [result-screen.md](./result-screen.md)
> 模式：[modes/](./modes/) · 計分：[scoring-hybrid.md](../../architecture/scoring-hybrid.md)  
> Phase 1 固定 **自由 + 向上**（見 [free-mode.md](./modes/free-mode.md)、[scroll-directions.md](./modes/scroll-directions.md)）
> 原版截圖：[assets/wireframes/05-game-arena/20230915_203143.png](../../../assets/wireframes/05-game-arena/20230915_203143.png)

## 3. 保留（Keep）

- 音符軌道 + 判定線核心玩法
- 方向鍵操作
- 即時判定反馈
- Combo 計數
- 局內分數顯示
- 結算排名
- 結算後回房

## 4. 改版 / 優化（Change）

| 項目 | MVP | 之後 | 原因 |
|------|-----|------|------|
| 遊戲模式 | 只做普通-向上 | 見 modes/ | 驗證核心 |
| 3D 角色動畫 | 簡化 2D 或 placeholder | P1 3D | 成本 |
| 背景場景 | 單一靜態背景 | 多場景 | 成本 |
| 跳舞毯支援 | 不做 | 不做 | 硬體適配 |
| 音符皮膚 | 預設 | P2 | 非核心 |
| ShowTime 段落 | 不做 | P2 | 簡化 |
| 回放 | Phase 1 不做 | P1 自由模式本地 `.rpl` | 參考 osu；見 [replay-local.md](../../systems/replay-local.md) |

## 5. UI 草稿

```
┌─────────────────────────────────────────┐
│  ♪ 測試歌曲 A          Combo: 42       │
│  分數: Alice 12500  Bob 9800  Carol 11200│
├─────────────────────────────────────────┤
│                                         │
│         （角色舞蹈區域）                  │
│                                         │
├─────────────────────────────────────────┤
│              ↑                          │
│         ←  ●  →     ← 判定線            │
│              ↓                          │
│         （音符飛入方向：向上）            │
└─────────────────────────────────────────┘

--- 結算（詳見 result-screen.md）---
  全員表格：排名/昵称/连击/P·C·B·M/命中率/积分/成绩(S~B)
  底部：120G + 1726经验  [确定]
  參考圖：assets/wireframes/05-game-arena/20230915_203143.png
```

## 6. 資料與狀態

| 時機 | 資料 |
|------|------|
| 進入前 | roomId, songId, mode, playerList |
| 遊戲中 | 即時 score, combo, judgments[] |
| 離開時 | finalScores[], ranks[], judgmentStats[] |

## 7. 邊界與錯誤

| 情況 | 處理 |
|------|------|
| 玩家斷線 | 標記斷線，結算 0 分或最後分數 |
| 所有人斷線 | 中止遊戲，房間回 Waiting |
| 延遲過高 | MVP 不做特殊處理，之後加 |
| 歌曲載入失敗 | 中止，回房間，提示 |

## 8. 待確認

- [ ] 原版判定窗口多寬？
- [x] 原版判定叫 Perfect / Cool / Bad / Miss（見 result-screen.md）
- [x] 結算後手動按「确定」回房
- [ ] 能不能在遊戲中退出？

## 子文件

- [result-screen.md](./result-screen.md) — 結算畫面（含原版考據 + 他遊戲參考）
- [modes/README.md](./modes/README.md) — 模式文件索引
- [modes/free-mode.md](./modes/free-mode.md) — 自由模式
- [modes/normal-mode.md](./modes/normal-mode.md) — 普通模式（MVP）
- [modes/scroll-directions.md](./modes/scroll-directions.md) — 向上 / 向下 / 傾斜
