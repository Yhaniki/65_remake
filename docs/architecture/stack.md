# 技術棧（Stack）

## 總覽

| 層 | 技術 | 用途 |
|----|------|------|
| 引擎 | **Unity** | 客户端、渲染、輸入、音訊 |
| 玩法參考 | **[osu lazer](https://github.com/ppy/osu)** / osu-framework | Ruleset、mania 譜、skin 概念 |
| SM 參考 | **[SM-YHANIKI](https://github.com/Yhaniki/SM-YHANIKI)** | NoteSkin、`.sm`、總按鍵數、逆流 scroll |
| 連線 | **FishNet** | 房間、同步、結算廣播 |
| 發行/登入 | **Facepunch Steamworks** | Steam 登入、成就、overlay |
| 後端 | **PlayFab** | 帳號、戰績、Normal 模式勝負、leaderboard |
| 譜面 runtime | **Canonical Chart** | 內部統一格式；SM/osu/GN import |

## osu lazer ≠ Unity 內嵌

[osu lazer](https://github.com/ppy/osu) 為 .NET 8 獨立 client。本專案：

- Unity 做 **runtime**
- 從 lazer / [osu-framework](https://github.com/ppy/osu-framework) **移植概念**（Ruleset、HitObject、skin 目錄）
- 必要時抽離 C# 解析為 `Remake.Osu`（Step 1）→ `Remake.Chart`（Phase 1）

## 借鑑對照

| 從 osu | 從 SM-YHANIKI | Unity 自建 |
|--------|---------------|------------|
| Ruleset 生命週期 | NoteSkin、`note(RV)` | 3D / VMD |
| `.osu` mania 解析 | 總按鍵數、連線比 note 數 | FishNet |
| Skin 結構 | Hold 結尾 Marvelous 白光 | Steam + PlayFab |
| 星數思路 | scroll 不受歌曲倍速影響 | 向上/向下/傾斜 |

## 階段與技術

| 階段 | 啟用 |
|------|------|
| [Step 1](../STEP1.md) | Unity + Remake.Osu + Ruleset；**純 .osu**，無 Canonical |
| [Phase 1](../PHASE1.md) | + Chart、Hold、選歌、結算 |
| MVP | + FishNet 房間 |
| 全案 | + Steam、PlayFab、Skin、VMD |

## 相關文件

- [repo-structure.md](repo-structure.md)
- [chart-format.md](chart-format.md)
- [online-services.md](online-services.md)
- [../reference/external-references.md](../reference/external-references.md)
