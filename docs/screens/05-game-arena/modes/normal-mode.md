# 普通模式

> **MVP P0**（Phase 1 用 [free-mode.md](./free-mode.md) 代替）  
> 父文件：[../spec.md](../spec.md)  
> 計分：[scoring-hybrid.md](../../../architecture/scoring-hybrid.md)  
> 方向：[scroll-directions.md](./scroll-directions.md)

## 模式說明

- **普通模式**：房主固定難度；**記錄勝負**（PlayFab）
- 方向（向上 / 向下 / 傾斜）獨立定義，與本模式正交組合

## 原版（Reference）

- 四軌 4K、Perfect / Cool / Bad / Miss
- 房主選曲、全員同難度

## 保留（Keep）

- 4 方向鍵
- P / C / B / M 判定
- Hold 頭端合併（scoring-hybrid）

## 改版（Change）

| 項目 | Phase 1 | MVP |
|------|---------|-----|
| 模式 | free-mode 代替 | 房間選普通 |
| 勝負 | — | PlayFab |
| 計分 | 1e8 hybrid | 同 |
| 方向 | — | 向上 P0；向下 / 傾斜 P1 |

## 操作

↑↓←→（Phase 1 可加 WASD P1）

## 相關

- [free-mode.md](./free-mode.md) — Phase 1 實際遊玩
- [result-screen.md](../result-screen.md)
