# 遊戲模式索引

> 維基：[熱舞Online 遊戲模式](https://zh.wikipedia.org/zh-tw/%E8%B6%85%E7%BA%A7%E8%88%9E%E8%80%85)

命名：**模式**（自由 / 普通）× **方向**（向上 / 向下 / 傾斜），正交組合。

## 文件

| 類別 | 文件 |
|------|------|
| 自由模式 | [free-mode.md](../screens/05-game-arena/modes/free-mode.md) |
| 普通模式 | [normal-mode.md](../screens/05-game-arena/modes/normal-mode.md) |
| 方向 | [scroll-directions.md](../screens/05-game-arena/modes/scroll-directions.md) |

## 重製優先（自由 + 普通）

| 模式 | 方向 | 優先級 |
|------|------|--------|
| 自由 | 向上 | **Phase 1** |
| 自由 | 向下 | P1 |
| 自由 | 傾斜 | P1 |
| 普通 | 向上 | **MVP P0** |
| 普通 | 向下 | P1 |
| 普通 | 傾斜 | P1 |

| 模式 | 難度 | 勝負 |
|------|------|------|
| 自由 | 玩家自選 | 不記錄 |
| 普通 | 房主固定 | PlayFab 記錄 |

## 原版其他模式（post-MVP）

| 模式 | 方向 | 優先級 |
|------|------|--------|
| 反鍵 | 三向 | P2 |
| 六鍵 | 三向 | P2 |
| 情侶 / 天舞 / 寵舞 / 炫彩 / ShowTime | 三向 | P2 |
| 怪物狩獵 / 寶石 / 徽章 | 三向 | P2 |

完整列表見維基；實作時在 `modes/` 新增文件。

## 索引

- [modes/README.md](../screens/05-game-arena/modes/README.md)
- [PHASE1.md](../PHASE1.md)
