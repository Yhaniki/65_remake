# Repo 目錄結構

> 規劃用。**Step 1** 只 scaffold 標記段落 — [STEP1.md](../STEP1.md)  
> **Classic / Enhanced = 兩個獨立 exe** — [dual-variant.md](dual-variant.md)

```
g:\65_remake\
├── README.md
├── docs/
├── assets/
├── tools/
└── src/
    ├── Remake.sln
    ├── Remake.Osu/                  # Step 1：純 .osu 解析
    ├── Remake.Chart/                # Phase 1：osu → Canonical
    ├── Remake.Ruleset/              # Step 1：Tap 判定
    ├── Remake.Classic/
    ├── Remake.Enhanced/
    ├── Remake.Replay/
    ├── Remake.Shared/
    ├── Remake.Skin/
    ├── Remake.Dance/
    ├── Remake.Platform/
    ├── Remake.Server.Classic/
    ├── Remake.Server.Enhanced/
    ├── Remake.Unity.Classic/
    └── Remake.Unity.Enhanced/       # Step 1：Step1.unity
        └── Assets/Scripts/Step1/
```

## 專案職責

| 專案 | Step 1 | Phase 1 | MVP+ |
|------|--------|---------|------|
| Remake.Osu | ✅ | ✅ | ✅ |
| Remake.Chart | — | ✅ | + SM/GN |
| Remake.Ruleset | ✅ Tap | + Hold/計分 | + 模式 |
| Remake.Unity.Enhanced | ✅ 單場景 | + 三屏 | 全 screens |
| Remake.Classic | — | stub | Classic exe |
| Remake.Enhanced | — | — | Enhanced exe |
| Remake.Server.* | — | — | MVP+ |

## 相關

- [stack.md](stack.md)
- [dual-variant.md](dual-variant.md)
- [STEP1.md](../STEP1.md)
- [PHASE1.md](../PHASE1.md)
