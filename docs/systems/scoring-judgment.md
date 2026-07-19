# 判定與計分（Scoring & Judgment）

> 完整公式：[architecture/scoring-hybrid.md](../architecture/scoring-hybrid.md)  
> 原版考據：[reverse-engineering/SDO_SCORE_FORMULA.md](../reverse-engineering/SDO_SCORE_FORMULA.md)

## 職責

- 音符判定（Perfect / Cool / Bad / Miss）
- Combo 與 Hold 頭端合併
- 混合計分（1e8 封頂 + SDO combo）
- 結算統計（P+C+B+M = totalNotes）

## 判定窗（ms，BPM 無關）

採 StepMania 的窗：**精4 為基準 × 1.33 = 精2**（`JudgmentWindows.FromStepManiaJudge`）。
精度可在 `config.ini` `[Room]` 的 `judgeLevel` 手改（1~8，9=JUSTICE；`RoomConfig.judgeLevel`）。
SM 五段折成四段：MARVELOUS+PERFECT → Perfect、GREAT → Cool、GOOD → Bad、BOO（含更外面）→ Miss。

| 判定 | 精4 基準 | 精2（實際） |
|------|----------|-------------|
| Perfect | ±45 ms | ±59.85 ms |
| Cool | ±90 ms | ±119.7 ms |
| Bad | ±135 ms | ±179.55 ms |
| Miss | ±180 ms | ±239.4 ms |

原版是 tick 制（BPM 越快越嚴），已停用但保留在 `JudgmentWindows.FromSdoBpm`。
完整對照：[reference/judgment-windows-sm-vs-sdo.md](../reference/judgment-windows-sm-vs-sdo.md)

## 判定

| 等級 | Combo（單獨） |
|------|---------------|
| Perfect | 延續 |
| Cool | 中斷 |
| Bad | 延續 |
| Miss | 中斷 |

Hold Bad/Miss head → 頭端合併，見 scoring-hybrid §Hold。

## 計分摘要

```
eventScore = baseValue × judgeMul × (combo + 1) / 2
總分封頂 100,000,000
```

## 結算資料

```
Result {
  totalScore, maxCombo
  judgments: { perfect, cool, bad, miss }
  accuracy, grade
}
```

詳見 [result-screen.md](../screens/05-game-arena/result-screen.md)。

## Phase 1

Remake.Ruleset 實作 tap + hold + 頭端合併 + 1e8。

## 相關

- [PHASE1.md](../PHASE1.md)
- [modes/normal-mode.md](../screens/05-game-arena/modes/normal-mode.md)
