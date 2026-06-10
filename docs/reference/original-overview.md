# 原版概覽

> 考據來源：[維基百科 — 熱舞Online](https://zh.wikipedia.org/zh-tw/%E8%B6%85%E7%BA%A7%E8%88%9E%E8%80%85)

## 基本資訊

| 項目 | 內容 |
|------|------|
| 台灣名稱 | 熱舞 Online |
| 中國名稱 | 超級舞者 |
| 類型 | 大型多人線上音樂遊戲 |
| 平台 | Windows |
| 開發/發行 | 久游網 |
| 上線 | 2007 年 3 月 |

## 核心特色（原版）

- **3D 虛擬人物** 舞蹈模擬，使用 Motion Capture 採集動作
- **操作方式**：電腦鍵盤，或 USB 跳舞毯
- **舞蹈風格**：HIP-HOP、爵士、桑巴、國標等
- **場景背景**：新天地、南京路步行街、重慶解放碑等時尚街景
- **全球首創**：跳舞毯 + 網路多人同步跳舞

## 遊戲結構（推測流程）

```
啟動 → 登入 → 選伺服器 → 大廳 → 房間 → 遊戲場地 → 結算 → 回房/回大廳
                ↓
         家族 / 商場 / 個人房（社交與經濟）
```

## 主要 Screen 對照

| 原版概念 | 規劃文件 |
|----------|----------|
| 登入介面 | [screens/01-login/spec.md](../screens/01-login/spec.md) |
| 伺服器選擇 | [screens/02-server-select/spec.md](../screens/02-server-select/spec.md) |
| 大廳 | [screens/03-lobby/spec.md](../screens/03-lobby/spec.md) |
| 房間 | [screens/04-room/spec.md](../screens/04-room/spec.md) |
| 遊戲場地 | [screens/05-game-arena/spec.md](../screens/05-game-arena/spec.md) |
| 家族公會 | [screens/06-guild/spec.md](../screens/06-guild/spec.md) |
| 商場 | [screens/07-shop/spec.md](../screens/07-shop/spec.md) |
| 個人房間 | [screens/08-personal-room/spec.md](../screens/08-personal-room/spec.md) |

## 重製技術參考

- Phase 1：[PHASE1.md](../PHASE1.md)
- 架構：[architecture/stack.md](../architecture/stack.md)
- SDO 考據：[reverse-engineering/README.md](../reverse-engineering/README.md)
- 外部：[external-references.md](external-references.md)

## 待補考據

- [x] 結算畫面 UI — 見 [result-screen.md](../screens/05-game-arena/result-screen.md) 與 `assets/wireframes/05-game-arena/`
- [ ] 各 screen 其他 UI 截圖或錄影
- [ ] 原版按鍵映射（方向鍵 vs WASD vs 數字鍵）
- [x] 原版判定名稱：Perfect / Cool / Bad / Miss
- [ ] 原版判定分數公式 → 見 [architecture/scoring-hybrid.md](../architecture/scoring-hybrid.md)
- [x] 結算房間至少支援 6 人位（截圖可見 6 列）
- [ ] 原版歌曲列表與授權方式

> 有原版截圖或回憶可補進對應 screen 的 spec.md「原版行為」區塊。
