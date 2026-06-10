# 譜面 Import（Beatmap Import）

## 策略

內部 **Canonical Chart**；外部格式 import 後 runtime 只讀 Chart。

見 [architecture/chart-format.md](../architecture/chart-format.md)。

## Import 路線

| 格式 | 階段 | 工具 |
|------|------|------|
| `.osu` mania 4K | **Phase 1** | `Remake.Chart` |
| `.sm` | P1 | `tools/converters/sm_to_chart` |
| `.gn` / StepFile | P2 | 見 [SM_GN_NOTE_FORMAT.md](../reverse-engineering/SM_GN_NOTE_FORMAT.md) |

## 可選匯出

`chart_to_osu` — 測試、分享。

## 音訊

- Phase 1：譜面夾內 `.ogg` / `.mp3`
- SDO `.sdm` → 見 [SDM_DECODE_GUIDE.md](../reverse-engineering/SDM_DECODE_GUIDE.md)（預處理，非 runtime）

## Phase 1

`StreamingAssets/Songs/{id}/` 手放 osu + audio。

## 相關

- [PHASE1.md](../PHASE1.md)
- [beatmap-import 工具路徑](../architecture/repo-structure.md)
