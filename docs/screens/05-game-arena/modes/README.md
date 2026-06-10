# 遊戲模式文件

## 文件結構

| 文件 | 內容 |
|------|------|
| [free-mode.md](./free-mode.md) | 自由模式（難度、勝負、用途） |
| [normal-mode.md](./normal-mode.md) | 普通模式（房主難度、PlayFab 勝負） |
| [scroll-directions.md](./scroll-directions.md) | 向上 / 向下 / 傾斜（各模式共用） |

**模式 × 方向** 正交組合，不再拆成 `free-up`、`normal-tilt` 等獨立檔。

## Phase 1

| 模式 | 方向 | 優先級 |
|------|------|--------|
| 自由 | 向上 | **P0 Phase 1** |

Phase 1 固定 **自由 + 向上**（單機無模式選單）。見 [free-mode.md](./free-mode.md)。

## MVP P0

| 模式 | 方向 | 優先級 |
|------|------|--------|
| 自由 | 向上 | P0 |
| 普通 | 向上 | P0 |

## P1

向下、傾斜 — 見 [scroll-directions.md](./scroll-directions.md)。

## 模式差異摘要

| | 自由 | 普通 |
|---|------|------|
| 難度 | 玩家自選 | 房主固定 |
| 勝負 | 不記錄 | PlayFab 記錄 |
| 用途 | 練習 | 房間競技 |

## 索引

全模式列表：[game-modes-index.md](../../../reference/game-modes-index.md)
