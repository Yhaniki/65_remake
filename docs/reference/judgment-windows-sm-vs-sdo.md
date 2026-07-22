# 判定時間窗對照：StepMania（YHANIKI 精1～精7） vs. 我們的 SDO

> 目的：把兩套判定「每一段涵蓋多少時間」攤平成同一張尺，方便調參與移植討論。
>
> 來源：
> - StepMania：`assets/SM-YHANIKI-master/src/PrefsManager.cpp:89-99`、`src/Player.cpp:56,742-747,1302-1305`、`src/ScreenOptionsMasterPrefs.cpp:289-293,490`
> - SDO：`FUN_0048c4a0`（判定函式）＋ caller `~0x497a60`；重製實作 [JudgmentWindows.cs](../../65/My%20project/Assets/Scripts/Sdo.Ruleset/JudgmentWindows.cs)、[ManiaJudgmentEngine.cs](../../65/My%20project/Assets/Scripts/Sdo.Ruleset/ManiaJudgmentEngine.cs)

## 0. 一句話結論

- **StepMania 的窗是「絕對秒數」**：以**精4 為基準**（22.5 / 45 / 90 / 135 / 180 ms），再乘上「精N」係數。**跟 BPM 無關**。
- **原版 SDO 的窗是「譜面 tick」**（1 tick = 1/192 小節）：固定 6 / 15 / 20 / 25 ticks，**換算成 ms 會隨 BPM 變**——歌越快窗越窄、越嚴格。
- 粗略感覺：原版 SDO 的 Perfect 大約落在「StepMania 精2（慢歌）～精5（快歌）的 PERFECT 段」；Cool/Bad 則比 StepMania 的 GREAT/GOOD 寬很多。

## 0.1 ⚠ 重製版現況（2026-07 起）

**已改成時間（ms）制，不再看 tick。** 採 StepMania 的窗、**精4 為基準乘 1.33 = 精2**，SM 五段折成 SDO 四段：

| SDO 判定 | 折自 SM | 精4 基準 | ×1.33 → **精2（實際使用）** |
|----------|---------|----------|------------------------------|
| **Perfect** | MARVELOUS + PERFECT | ±45 ms | **±59.85 ms** |
| **Cool** | GREAT | ±90 ms | **±119.7 ms** |
| **Bad** | GOOD | ±135 ms | **±179.55 ms** |
| **Miss** | BOO ＋ 超出 BOO | ±180 ms | **±239.4 ms**（再外面 = 此按不吃，音符過線自動 Miss） |

實作：[JudgmentWindows.FromStepManiaJudge()](../../65/My%20project/Assets/Scripts/Sdo.Ruleset/JudgmentWindows.cs)。

**精度可手改**：`config.ini`（**全域一份**，在存檔層 `<data root>/DATA/PROFILE/`；佈局見
[game-settings.md](../systems/game-settings.md#本地永遠寫入--實作現況)）的 `[Room]` 區：

```ini
[Room]
# 判定精度（StepMania 的「精N」）：1~8，9=JUSTICE。數字越大越嚴格。
judgeLevel=2
```

（`RoomConfig.judgeLevel`，夾在 1~9；`ScreenGameplay` 載譜時讀。）
原版 tick 制留在 `JudgmentWindows.FromSdoBpm()` 供對照，沒有接進遊戲。以下第 1、2 節是兩套機制的原始考據。

---

## 1. StepMania（YHANIKI）判定機制

判定用**單次按鍵與音符的時間差絕對值**（`fSecondsFromPerfect`），對稱吃早/晚兩側：

```cpp
// Player.cpp:56
#define ADJUSTED_WINDOW( judge ) \
    ((PREFSMAN->m_fJudgeWindowSeconds##judge * PREFSMAN->m_fJudgeWindowScale) + PREFSMAN->m_fJudgeWindowAdd)

// Player.cpp:742-747
if      ( fSecondsFromPerfect <= ADJUSTED_WINDOW(Marvelous) ) score = TNS_MARVELOUS;
else if ( fSecondsFromPerfect <= ADJUSTED_WINDOW(Perfect)   ) score = TNS_PERFECT;
else if ( fSecondsFromPerfect <= ADJUSTED_WINDOW(Great)     ) score = TNS_GREAT;
else if ( fSecondsFromPerfect <= ADJUSTED_WINDOW(Good)      ) score = TNS_GOOD;
else if ( fSecondsFromPerfect <= ADJUSTED_WINDOW(Boo)       ) score = TNS_BOO;
else                                                           score = TNS_NONE;  // 這一按不吃這顆
```

基準值（`PrefsManager.cpp:92-99`，可被 `Preferences.ini` 覆寫）：

| 段位 | 基準（精4 = scale 1.0） |
|------|------------------------|
| MARVELOUS | ±22.5 ms |
| PERFECT | ±45 ms |
| GREAT | ±90 ms |
| GOOD | ±135 ms |
| BOO | ±180 ms |
| OK（hold 可鬆手上限） | 250 ms |
| Mine（地雷） | ±90 ms |
| Attack | ±135 ms |
| `JudgeWindowAdd` | 0（整體平移量，預設 0） |

「精N」= 選項 `Judge Difficulty`，只是換掉 `m_fJudgeWindowScale`（`ScreenOptionsMasterPrefs.cpp:289-293`）：

| 選項 | 精1 | 精2 | 精3 | 精4（預設） | 精5 | 精6 | 精7 | 精8 | JUSTICE |
|------|-----|-----|-----|-----|-----|-----|-----|-----|---------|
| scale | 1.50 | 1.33 | 1.16 | **1.00** | 0.84 | 0.66 | 0.50 | 0.33 | 0.20 |

Miss：沒打到的音符，過了 **BOO 窗 × 音樂倍速**就自動 MISS（`Player.cpp:310,1302-1305`）。
音樂倍速：按鍵誤差有除以 `m_fMusicRate`（`Player.cpp:688`）→ **開倍速時，實際時間窗不變**。

### 1.1 精1～精7：每段涵蓋的時間（ms，±對稱）

**每段的「上界」（誤差 ≤ 此值即為該判定）：**

| 段位 | 精1 | 精2 | 精3 | 精4 | 精5 | 精6 | 精7 |
|------|-----|-----|-----|-----|-----|-----|-----|
| MARVELOUS | ±33.8 | ±29.9 | ±26.1 | ±22.5 | ±18.9 | ±14.9 | ±11.3 |
| PERFECT | ±67.5 | ±59.9 | ±52.2 | ±45.0 | ±37.8 | ±29.7 | ±22.5 |
| GREAT | ±135.0 | ±119.7 | ±104.4 | ±90.0 | ±75.6 | ±59.4 | ±45.0 |
| GOOD | ±202.5 | ±179.6 | ±156.6 | ±135.0 | ±113.4 | ±89.1 | ±67.5 |
| BOO | ±270.0 | ±239.4 | ±208.8 | ±180.0 | ±151.2 | ±118.8 | ±90.0 |
| OK（hold） | 375 | 332.5 | 290 | 250 | 210 | 165 | 125 |
| Mine | ±135.0 | ±119.7 | ±104.4 | ±90.0 | ±75.6 | ±59.4 | ±45.0 |

**每段實際「涵蓋的區間」（誤差落在此區間 → 該判定）：**

| 段位 | 精1 | 精2 | 精3 | 精4 | 精5 | 精6 | 精7 |
|------|-----|-----|-----|-----|-----|-----|-----|
| MARVELOUS | 0–33.8 | 0–29.9 | 0–26.1 | 0–22.5 | 0–18.9 | 0–14.9 | 0–11.3 |
| PERFECT | 33.8–67.5 | 29.9–59.9 | 26.1–52.2 | 22.5–45.0 | 18.9–37.8 | 14.9–29.7 | 11.3–22.5 |
| GREAT | 67.5–135.0 | 59.9–119.7 | 52.2–104.4 | 45.0–90.0 | 37.8–75.6 | 29.7–59.4 | 22.5–45.0 |
| GOOD | 135.0–202.5 | 119.7–179.6 | 104.4–156.6 | 90.0–135.0 | 75.6–113.4 | 59.4–89.1 | 45.0–67.5 |
| BOO | 202.5–270.0 | 179.6–239.4 | 156.6–208.8 | 135.0–180.0 | 113.4–151.2 | 89.1–118.8 | 67.5–90.0 |
| 超出 BOO | >270 | >239.4 | >208.8 | >180 | >151.2 | >118.8 | >90 |

「超出 BOO」＝這一按**完全不吃這顆音符**（`TNS_NONE`，不扣不加）；該音符本身晚於 BOO 窗未被打到才會判 MISS。

> 註：MARVELOUS 只在 `ShowMarvelous()` 時顯示，否則降級成 PERFECT（`Player.cpp:857-858`）；也就是說某些模式下實際只有 5 段。

---

## 2. 原版 SDO 判定機制（tick 制；重製版已不採用，留作考據）

原版是 **tick 制**：誤差 `e = |playhead − notePos|`，其中 `notePos = bar*192 + tickInBar`（**192 ticks/小節**）。播放頭前進速率 `ticks/sec = BPM × 0.8`，所以

```
1 tick = 1250 / BPM  (ms)
```

窗（ticks，對稱吃早/晚）：

| 段位 | 一般模式 | 窄窗（narrow / 練習旗標） |
|------|---------|--------------------------|
| Perfect | e ≤ 6 | e ≤ 5 |
| Cool | 7 – 15 | 6 – 10 |
| Bad | 16 – 20 | 11 – 16 |
| Miss（有登記、斷 combo） | 21 – 25 | 17 – 20 |
| 不吃這一按 | > 25 | > 20 |

換算成 ms 的封閉式（一般模式）：

```
Perfect 上界 = 7500  / BPM   ms
Cool    上界 = 18750 / BPM   ms
Bad     上界 = 25000 / BPM   ms
可判定上界   = 31250 / BPM   ms      （超過 = 此按無效）
```
窄窗版：`6250 / BPM`、`12500 / BPM`、`20000 / BPM`、`25000 / BPM`。

實作：[JudgmentWindows.FromSdoBpm()](../../65/My%20project/Assets/Scripts/Sdo.Ruleset/JudgmentWindows.cs)（**目前沒有接進遊戲**，保留供比較／日後想切回原版手感）。判定→combo 的規則不變：Perfect/Cool 續 combo、Bad/Miss 斷 combo（見 [SDO_SCORE_FORMULA.md](../reverse-engineering/SDO_SCORE_FORMULA.md)）。

### 2.1 一般模式：每段涵蓋的時間（ms，±對稱）

| BPM | 1 tick | Perfect | Cool | Bad | Miss（仍登記） | 超過此值＝無效 |
|-----|--------|---------|------|-----|----------------|----------------|
| 90 | 13.9 | 0–83.3 | 83.3–208.3 | 208.3–277.8 | 277.8–347.2 | >347.2 |
| 100 | 12.5 | 0–75.0 | 75.0–187.5 | 187.5–250.0 | 250.0–312.5 | >312.5 |
| 110 | 11.4 | 0–68.2 | 68.2–170.5 | 170.5–227.3 | 227.3–284.1 | >284.1 |
| 120 | 10.4 | 0–62.5 | 62.5–156.3 | 156.3–208.3 | 208.3–260.4 | >260.4 |
| 130 | 9.6 | 0–57.7 | 57.7–144.2 | 144.2–192.3 | 192.3–240.4 | >240.4 |
| 140 | 8.9 | 0–53.6 | 53.6–133.9 | 133.9–178.6 | 178.6–223.2 | >223.2 |
| 150 | 8.3 | 0–50.0 | 50.0–125.0 | 125.0–166.7 | 166.7–208.3 | >208.3 |
| 160 | 7.8 | 0–46.9 | 46.9–117.2 | 117.2–156.3 | 156.3–195.3 | >195.3 |
| 170 | 7.4 | 0–44.1 | 44.1–110.3 | 110.3–147.1 | 147.1–183.8 | >183.8 |
| 180 | 6.9 | 0–41.7 | 41.7–104.2 | 104.2–138.9 | 138.9–173.6 | >173.6 |
| 190 | 6.6 | 0–39.5 | 39.5–98.7 | 98.7–131.6 | 131.6–164.5 | >164.5 |
| 200 | 6.3 | 0–37.5 | 37.5–93.8 | 93.8–125.0 | 125.0–156.3 | >156.3 |

### 2.2 窄窗（5/10/16/20 ticks）

| BPM | Perfect | Cool | Bad | Miss（仍登記） |
|-----|---------|------|-----|----------------|
| 120 | 0–52.1 | 52.1–104.2 | 104.2–166.7 | 166.7–208.3 |
| 150 | 0–41.7 | 41.7–83.3 | 83.3–133.3 | 133.3–166.7 |
| 180 | 0–34.7 | 34.7–69.4 | 69.4–111.1 | 111.1–138.9 |

（目前遊戲端固定用一般模式，`narrow` 旗標留著沒接 UI。）

---

## 3. 直接對照（StepMania vs **原版** SDO）

> 這一節比的是「原版 tick 制」與 StepMania；重製版現在用的窗見 §0.1。留著是為了回答「原版到底多嚴」。

### 3.1 最嚴那一段（頂級判定）

SDO Perfect 的 ms = `7500 / BPM`。把它換算成「等於 StepMania 哪個精度的 PERFECT 窗」（`45 × scale`）：

| BPM | SDO Perfect | 等效 SM scale | ≈ 精N（比 PERFECT 段） |
|-----|-------------|---------------|------------------------|
| 100 | ±75.0 ms | 1.67 | 比精1 還鬆 |
| 120 | ±62.5 ms | 1.39 | 精1～精2 之間 |
| 140 | ±53.6 ms | 1.19 | ≈ 精3 |
| 160 | ±46.9 ms | 1.04 | ≈ 精4（預設） |
| 180 | ±41.7 ms | 0.93 | 精4～精5 之間 |
| 200 | ±37.5 ms | 0.83 | ≈ 精5 |

**注意**：SDO 的 Perfect 對應的是 SM 的 **PERFECT 段**，不是 MARVELOUS。要讓 SDO Perfect 窄到等於 SM 的 MARVELOUS（`22.5 × scale`），BPM 得 ≥ 222（精1）甚至更高——換句話說 **SDO 沒有「白光」等級的嚴格度**。

### 3.2 同一張尺（BPM 150 vs 精4）

| 誤差 (ms) | SM 精4 | SDO @150 BPM |
|-----------|--------|--------------|
| 0–22.5 | MARVELOUS | Perfect |
| 22.5–45 | PERFECT | Perfect |
| 45–50 | GREAT | Perfect |
| 50–90 | GREAT | Cool |
| 90–125 | GOOD | Cool |
| 125–135 | GOOD | Bad |
| 135–166.7 | BOO | Bad |
| 166.7–180 | BOO | Miss（斷 combo） |
| 180–208.3 | 不吃這按 | Miss（斷 combo） |
| >208.3 | 不吃這按 | 不吃這按 |

可以看到 SDO 整體「可判定範圍」比 SM 精4 寬（±208 vs ±180），而且中間段（Cool/Bad）非常寬鬆——SDO 是「容易打到、但要 Perfect 也不算難」的手感；StepMania 精4 則是分段細、白光嚴。

### 3.3 結構差異

| 項目 | StepMania | 原版 SDO | **重製版（現在）** |
|------|-----------|----------|--------------------|
| 窗的單位 | 絕對秒（BPM 無關） | 譜面 tick（BPM 越快越嚴） | **絕對 ms（BPM 無關）＝ SM 精4 基準 × 1.33** |
| 段數 | 6 段（Marvelous/Perfect/Great/Good/Boo）＋ Miss | 4 段（Perfect/Cool/Bad/Miss） | 4 段（SM 五段折疊而來，見 §0.1） |
| 難度調整 | 玩家選精1～精8/JUSTICE（乘 scale） | 無；只有 normal / narrow 兩組硬編 tick | `smJudgeLevel`（1~8、9=JUSTICE），預設 2 |
| Combo | Great 以上續 | **Perfect / Cool 續；Bad / Miss 斷** | 同原版（Perfect/Cool 續） |
| Hold | 鬆手超過 OK 窗（250 ms × scale）才掉 | 頭端合併，尾端以放開時間判 | 同原版（尾端用同一組窗） |
| 音樂倍速 | 誤差除以 rate → **實際時間窗不變** | 誤差以譜面時間算 | 誤差仍以譜面時間算 → **開倍速時實際時間窗等比收緊**（未對齊 SM，見下） |

> **開放問題（未動）**：F9「遊戲流速」是把 chart timeline 加速（`GameRate`），判定誤差量在譜面時間上，因此 2× 流速 = 實際容許時間減半。StepMania 明確做了 `/ m_fMusicRate` 讓真實時間窗不變。要不要對齊，看我們想要「變速＝更難」還是「變速＝只是快」。

---

## 相關

- [sm-yhaniki-notes.md](sm-yhaniki-notes.md)
- [architecture/scoring-hybrid.md](../architecture/scoring-hybrid.md)
- [systems/scoring-judgment.md](../systems/scoring-judgment.md)
- [reverse-engineering/SDO_SCORE_FORMULA.md](../reverse-engineering/SDO_SCORE_FORMULA.md)（判定→分數／combo）
- [reverse-engineering/SDO_HP_FORMULA.md](../reverse-engineering/SDO_HP_FORMULA.md)（判定→血量）
