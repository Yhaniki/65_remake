# MVP 功能清單

> **Phase 1**（選歌→遊玩→結算）見 [PHASE1.md](PHASE1.md)。  
> **先做 [STEP1.md](STEP1.md)** — 1 歌 · 純 osu · 判定。

## 優先級

| 級別 | 意思 |
|------|------|
| **P0** | MVP 必做 |
| **P1** | MVP 加分 |
| **P2** | post-MVP |

---

## P0 — MVP 必做

### 流程

- [ ] Steam 登入（PlayFab 綁定）
- [ ] 伺服器選擇（可單服）
- [ ] 大廳（房間列表）
- [ ] 房間（模式/方向/難度、準備、開始）
- [ ] 遊玩（自由或普通 + 向上，至少各可玩）
- [ ] 結算 → 回房 → 回大廳

### 模式（P0 各至少 1 方向）

| 模式 | 難度 | 勝負 | 文件 |
|------|------|------|------|
| **自由** | 玩家自選 | 不記錄 | [modes/free-mode.md](screens/05-game-arena/modes/free-mode.md) |
| **普通** | 房主固定 | PlayFab 記錄 | [modes/normal-mode.md](screens/05-game-arena/modes/normal-mode.md) |

方向見 [scroll-directions.md](screens/05-game-arena/modes/scroll-directions.md)；**向下 / 傾斜** → P1。

### 系統

- [ ] Steam + PlayFab — [account-auth.md](systems/account-auth.md)
- [ ] FishNet 房間 — [room-matchmaking.md](systems/room-matchmaking.md)、[networking.md](systems/networking.md)
- [ ] 混合計分 — [architecture/scoring-hybrid.md](architecture/scoring-hybrid.md)
- [ ] osu import — [beatmap-import.md](systems/beatmap-import.md)

### Screens

| # | Screen |
|---|--------|
| 01–05 | login → game-arena（見 `screens/`） |

---

## P1

- [ ] 向下 / 傾斜 scroll（6 模式組合其餘）
- [ ] 自由模式 **本地 best + 回放**（osu 風 `.rpl`）— [replay-local.md](systems/replay-local.md)
- [ ] SM `.sm` import
- [ ] 預設 skin + 切換
- [ ] 大廳 **設定**（OPTION：鍵位、音效含 global offset、視窗/流速）— [game-settings.md](systems/game-settings.md)
- [ ] **Debug Overlay**（FPS、Ping、Loss 等可開關）— [debug-overlay.md](systems/debug-overlay.md)
- [ ] Hold Marvelous 白光（SM 風）

---

## P2 — post-MVP

| 功能 | 文件 |
|------|------|
| 家族 | [06-guild](screens/06-guild/spec.md) |
| 商場 | [07-shop](screens/07-shop/spec.md) |
| 個人房（咕盒風） | [08-personal-room](screens/08-personal-room/spec.md) · [personal-room-gogh.md](architecture/personal-room-gogh.md) |
| GN import | [beatmap-import.md](systems/beatmap-import.md) |
| VMD 舞蹈 | [dance-performance.md](systems/dance-performance.md) |

---

## 取捨

| 不做（MVP） | 原因 |
|-------------|------|
| 全部 30+ 原版模式 | 先自由+普通 |
| 跳舞毯 | 鍵盤先驗證 |
| 完整商城 | Phase 1/MVP 非必要 |

## 階段對照

| 階段 | 範圍 |
|------|------|
| Phase 1 | 選歌→遊玩→結算 |
| MVP | + 線上大厅流程 |
| 全案 | + skin/VMD/個人房/SM/GN |
