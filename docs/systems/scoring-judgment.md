# 判定與計分（Scoring & Judgment）

> 完整公式：[architecture/scoring-hybrid.md](../architecture/scoring-hybrid.md)  
> 原版考據：[reverse-engineering/SDO_SCORE_FORMULA.md](../reverse-engineering/SDO_SCORE_FORMULA.md)

## 職責

- 音符判定（Perfect / Cool / Bad / Miss）
- Combo 與 Hold 頭端合併
- 混合計分（1e8 封頂 + SDO combo）
- 結算統計（P+C+B+M = totalNotes）

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
