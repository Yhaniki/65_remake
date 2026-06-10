# osu!mania 星數（Star Rating）計算方式

> 本文件整理 osu! lazer 官方原始碼（`bak/osu-master`）中 **osu!mania** ruleset 的星數演算法，作為日後在 bms 專案實作「鍵盤音遊難度估算」時的參考。
>
> 演算法版本：`Version = 20241007`（見 `ManiaDifficultyCalculator.cs`）。

---

## 1. 前提觀念

- **`.osu` 譜面檔本身不包含星數**。星數是玩家端（或 osu! 官網）讀完整張譜後，於**執行階段**依演算法即時計算出來的。
- 因此同一張譜在不同演算法版本下，算出的星數會不同。
- 只有少數 mod 會改變星數：`DoubleTime / HalfTime / Easy / HardRock`，以及 convert 模式下的各 `KeyMod`；其餘 mod 不影響。

原始碼位置：

```
bak/osu-master/osu.Game.Rulesets.Mania/Difficulty/
├─ ManiaDifficultyCalculator.cs          ← 入口（本文 §2）
├─ ManiaDifficultyAttributes.cs
├─ Preprocessing/ManiaDifficultyHitObject.cs  ← note 預處理（§3）
├─ Skills/Strain.cs                      ← 主技能，累積 strain（§4）
└─ Evaluators/
   ├─ IndividualStrainEvaluator.cs       ← 每軌獨立壓力（§4.1）
   └─ OverallStrainEvaluator.cs          ← 全盤整體壓力（§4.2）

bak/osu-master/osu.Game/Rulesets/Difficulty/Skills/
├─ StrainSkill.cs                        ← 切區段 + 加權總和（§5、§6）
└─ StrainDecaySkill.cs
```

---

## 2. 入口公式

在 `ManiaDifficultyCalculator.CreateDifficultyAttributes`：

```csharp
StarRating = skills.OfType<Strain>().Single().DifficultyValue() * difficulty_multiplier;
```

其中 `difficulty_multiplier = 0.018`。

```
StarRating = Strain.DifficultyValue() × 0.018
```

osu!mania 只使用**一個** `Strain` skill，不像 osu!standard 有 Aim / Speed / Flashlight 等多個 skill 組合，演算法結構相對單純。

---

## 3. note 預處理（`ManiaDifficultyHitObject`）

`CreateDifficultyHitObjects` 流程：

1. 把 `beatmap.HitObjects` 依 `StartTime` 排序。
2. 從第二個 note 開始，每個 note 封裝成一個 `ManiaDifficultyHitObject`，記錄：
   - `Column`：在哪一軌（K1, K2, …）。
   - `ColumnStrainTime = StartTime − 同軌上一個 note 的 StartTime`（同軌間隔，越小越難）。
   - `PreviousHitObjects[column]`：每條軌最近一顆 note（用來判定手上是否正壓著長條）。

> 注意：**第一顆 note 不會產生 strain**。

---

## 4. 主技能 `Strain`（累積壓力）

`Strain.StrainValueOf` 每收到一顆 note 就更新兩個內部狀態，再輸出該 note 的 strain 值：

```csharp
individualStrains[col] = applyDecay(individualStrains[col], ColumnStrainTime, 0.125);
individualStrains[col] += IndividualStrainEvaluator.EvaluateDifficultyOf(current);

highestIndividualStrain = (DeltaTime <= 1)
    ? Math.Max(highestIndividualStrain, individualStrains[col])  // 和弦：取最高
    : individualStrains[col];

overallStrain = applyDecay(overallStrain, DeltaTime, 0.30);
overallStrain += OverallStrainEvaluator.EvaluateDifficultyOf(current);

return highestIndividualStrain + overallStrain − CurrentStrain;
```

兩種 strain：

| 名稱 | 含義 | 衰減底數（每秒剩下） |
|---|---|---|
| `individualStrains[column]` | **每軌獨立**的壓力，和同軌連點間距有關 | `0.125`（一秒後剩 12.5%，衰很快） |
| `overallStrain` | **全盤整體**壓力，每顆 note 都會累積 | `0.30`（一秒後剩 30%，衰較慢） |

衰減公式：

```
value ← value × decayBase^(Δt / 1000)
```

- `individualStrains` 用 **同軌間隔 `ColumnStrainTime`** 衰減（每軌獨立節奏）。
- `overallStrain` 用 **整體間隔 `DeltaTime`** 衰減（全盤節奏）。

**和弦處理**：`DeltaTime ≤ 1 ms` 視為同時按下的和弦，`highestIndividualStrain` 取所有同時 note 的最高值，避免處理順序影響結果。

**末端扣 `CurrentStrain`**：這是 `StrainDecaySkill` 的技巧，讓每個 400 ms 區段只取**一顆 note 的最大壓力**，不重複累加。

### 4.1 `IndividualStrainEvaluator`（每軌壓力）

```csharp
return 2.0 * holdFactor;
```

- 基本值固定 `2.0`。
- 如果這顆 note **完全夾在另一軌長條（Long Note）的持續期間**內（手被佔著還要按別的軌），`holdFactor = 1.25`，否則為 `1.0`。

### 4.2 `OverallStrainEvaluator`（全盤壓力）

```csharp
return (1 + holdAddition) * holdFactor;
```

- 基本值 `1.0`，`holdFactor` 同上（被長條佔著時 ×1.25）。
- 若此 note 的 body 和其他長條「互相重疊但結束時間錯開」，視為 `isOverlapping = true`。
- `holdAddition` 以 **Logistic 函數**取值（懲罰長條尾巴不對齊的情形）：

```
holdAddition = logistic(x = closestEndTime, multiplier = 0.27, midpointOffset = 30)
```

其中 `closestEndTime` = 當前 note 結束時間與最近一顆前置 note 結束時間的差（ms）。當兩尾端差距接近 `30 ms` 以上時，`holdAddition` 會趨近 1，意味著「尾巴錯開很難放」；若大家一起放，就沒有懲罰。

---

## 5. 切 400 ms 區段、保留峰值（`StrainSkill.Process`）

每收到一顆 note 就把 strain 塞進當前區段，**只保留該區段的最大值**；跨越 400 ms 邊界時把峰值存起來、重置為區段起點的 strain（經過衰減）。

```csharp
protected virtual int SectionLength => 400;   // ms

while (current.StartTime > currentSectionEnd)
{
    strainPeaks.Add(currentSectionPeak);
    currentSectionPeak = CalculateInitialStrain(currentSectionEnd, current);
    currentSectionEnd += SectionLength;
}

double strain = StrainValueAt(current);
currentSectionPeak = Math.Max(strain, currentSectionPeak);
```

結果：一張譜產出一串 `strainPeaks`（每 400 ms 一個峰值）。

---

## 6. 加權排序總和 = `DifficultyValue`

```csharp
protected virtual double DecayWeight => 0.9;

public override double DifficultyValue()
{
    double difficulty = 0, weight = 1;
    var peaks = GetCurrentStrainPeaks().Where(p => p > 0);
    foreach (double strain in peaks.OrderDescending())
    {
        difficulty += strain * weight;
        weight *= DecayWeight;
    }
    return difficulty;
}
```

把所有區段峰值**由大到小排序**後做幾何加權和：

```
DifficultyValue = peak[0]·1 + peak[1]·0.9 + peak[2]·0.81 + peak[3]·0.729 + …
```

這等於一種「以最難的一段為主，較難的區段拉抬、較容易的區段貢獻遞減」的聚合方式；這是 osu! 所有 ruleset 共用的經典公式。

> `p > 0` 的過濾是為了避免譜面極端情況下排序成本爆炸（見官方註解提到的 beatmap `/b/2351871`）。

---

## 7. 完整式子（一行濃縮）

```
對每顆 note i：
  strain_i = highestIndividualStrain(col, 衰減 0.125/s)
           + overallStrain(衰減 0.30/s)
           − CurrentStrain

以 400 ms 為單位切段，每段取最大 → peaks[k]
由大到小排序 peaks：
  DifficultyValue = Σ peaks_sorted[k] × 0.9^k   (k = 0, 1, 2, …)

StarRating = DifficultyValue × 0.018
```

---

## 8. 其他相關細節

### 8.1 MaxCombo

`ManiaDifficultyCalculator.maxComboForObject`：

- 普通 note：`1`
- 長條 note：`1 + (EndTime − StartTime) / 100`

### 8.2 受影響的 Mod

```
ManiaModDoubleTime / ManiaModHalfTime / ManiaModEasy / ManiaModHardRock
```

Convert 模式（把 osu!standard 譜轉成 mania）時另外加入所有 `KeyMod`（K1~K9，及搭配 DualStages）。

其餘 mod 不會影響星數。

### 8.3 DoubleTime / HalfTime 的影響方式

不是硬乘一個倍率，而是改變 `clockRate`（整張譜的時間被壓縮或拉長），
`DeltaTime` 與 `ColumnStrainTime` 都會跟著變小或變大，**間接**改變衰減量與 strain 峰值，從而改變星數。

---

## 9. 對 bms 專案的啟示

如果要在 bms 做類似的難度估算，可直接套用此流程：

1. **排序 + 預處理**
   依時間排序所有 note；同時建立「每軌上一顆 note」的索引。
2. **計算單顆 strain**
   對每顆 note：
   - 更新所在軌道的 `individualStrain`（衰減 + 基底 2.0，手被佔著時 ×1.25）。
   - 更新全盤 `overallStrain`（衰減 + 基底 1.0，加上長條尾端錯開懲罰）。
3. **切段**：以固定秒數（例如 400 ms）為區段，只留各段最大值。
4. **加權**：排序後以 `0.9^k` 做幾何加權和，再乘以適合 bms 的常數（osu 用 0.018）。

若 bms 譜還需要考慮 keysound 密度、scratch、BPM gimmick 等，可在 evaluator 層加上額外項，而維持上述「每顆 note 貢獻 → 分段峰值 → 加權總和」的整體骨架。
