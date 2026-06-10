# 熱舞 Online 重製版 — 規劃文件

Unity 重製。**Classic**、**Enhanced** 編譯成 **兩個獨立程式**，共用底層 DLL；玩法參考 [osu lazer](https://github.com/ppy/osu) + [SM-YHANIKI](https://github.com/Yhaniki/SM-YHANIKI)。見 [dual-variant.md](docs/architecture/dual-variant.md)。

## 開發階段

| 階段 | 範圍 | 文件 |
|------|------|------|
| **Step 1** | 1 首歌 · Tap · 純 `.osu` · 打判定 | [STEP1.md](docs/STEP1.md) |
| **Phase 1** | + 選歌 → Hold → 計分 → 結算 | [PHASE1.md](docs/PHASE1.md) |
| **MVP** | + 登入 → 大廳 → 房間 → 多人 | [MVP.md](docs/MVP.md) |
| **全案** | + Classic exe · Steam · VMD… | [PROJECT.md](docs/PROJECT.md) |

## 技術棧

Unity · FishNet · Facepunch Steamworks · PlayFab · Canonical Chart

詳見 [docs/architecture/stack.md](docs/architecture/stack.md)。

## 從哪裡開始讀

1. [docs/STEP1.md](docs/STEP1.md) — **現在：** 1 歌 · 純 osu · 判定
2. [docs/PHASE1.md](docs/PHASE1.md) — 選歌 → 結算
3. [docs/MVP.md](docs/MVP.md) — 完整 MVP

## 目錄結構

```
docs/
├── STEP1.md            # 第一步：1 歌 osu 判定
├── PHASE1.md
├── PROJECT.md / MVP.md
├── architecture/       # 技術、repo、計分、skin
├── reverse-engineering/  # SDO 反編譯考據（扁平）
├── screens/ / flows/ / systems/
├── reference/ / shared/
assets/wireframes/
src/                    # Phase 1 起（見 repo-structure.md）
```
