# StepMania 負 BPM（warp）

> 實作：[`SmChart.cs`](../../65/My%20project/Assets/Scripts/Sdo.Osu/SmChart.cs)（`SmWarp` / `Timeline` / `DisplayStops`）、
> [`OsuHitObject.cs`](../../65/My%20project/Assets/Scripts/Sdo.Osu/OsuHitObject.cs)（`IsFake` / `ScrollTimeMs`）、
> `ScreenGameplay.ScrollNotes`。測試：`Assets/Tests/EditMode/SmChartTests.cs`。
> 出處：`assets/SM-YHANIKI-master/src/TimingData.cpp`（StepMania 3.9）。

## 1. 這是什麼

`.sm` 的 `#BPMS` 允許**負值**。負 BPM 段的「經過時間」是負的，也就是歌曲時間**倒退**；
要等後面**同樣時間長度**的正 BPM 把它加回來，播放頭才回到原本的時刻，之後的譜面就照原時間接上。

```
#BPMS:0.000=120.000,4.000=-120.000,8.000=120.000
```

| beat | `GetElapsedTimeFromBeat` |
|------|--------------------------|
| 0    | 0 ms |
| 4    | 2000 ms  ← 起跳 |
| 8    | 0 ms     ← 倒退了 2000 ms |
| 12   | 2000 ms  ← 補回來了，落地 |
| 13   | 2500 ms  ← 後面照原時間接上 |

播放時 StepMania 走的是反方向的函式 `TimingData::GetBeatAndBPSFromElapsedTime`：
負段的 `fSecondsInThisSegment` 是負的，`fElapsedTime <= fSecondsInThisSegment` 永遠不成立，
所以那一段直接被跳過，而且 `fElapsedTime -= fSecondsInThisSegment` 反而把時間**加**回去，
一口氣衝進後面的正段。結果就是：**歌曲時間走到 2000 ms 的那一瞬間，拍子從 4 直接跳到 12**。

被跳過的 `(4, 12)` 這段就是一次 **warp**（`SmChart.SmWarp`）：

* `StartBeat` = 起跳拍（**打得到** —— 播放頭正是在這一拍起跳的）
* `EndBeat` = 落地拍（**打得到**）
* `TimeMs` = 起跳與落地共用的那**一個**歌曲時刻
* 中間的拍子 → 看得到、打不到

負 BPM 一路到譜尾、永遠沒被抵銷的話，warp 就收在譜尾，後面整段都是打不到的。

## 2. 三種「時間」

`SmChart.Timeline` 內部把時間分成三種，不能混用：

| | 意義 | warp 內的值 |
|---|---|---|
| `RawMs(beat)` | StepMania `GetElapsedTimeFromBeat` 原式 | 會倒退（負斜率） |
| `PlayMs(beat)` | 播放頭**真的**經過那一拍的時刻 → `OsuHitObject.StartTimeMs`（判定） | 一律 = `warp.TimeMs` |
| `DisplayMs(beat)` | 畫面定位用 → `OsuHitObject.ScrollTimeMs` | 攤在 `[TimeMs − 1ms, TimeMs)` 這個窗裡；**落地那一拍一律對齊窗尾**（見 §4） |

`RawMs` 之外都是**單調不減**的，所以音符排序、`NoteScan` 的視窗掃描都照舊。

## 3. 為什麼判定時間和顯示時間要分家

StepMania 3.9 的音符位置是 **beat spacing**：`ArrowEffects::ArrowGetYOffset` 直接用
`fNoteBeat − fSongBeat`。所以 warp 內的音符在進場時是**照拍子一顆顆排開**往下捲的，
到了那個瞬間才整批刷過判定線 —— 玩家看得到它們，只是碰不到。

本專案的高速公路是**用時間定位**音符的（`ManiaScroll.PixelDistance(now, noteMs)`）。
warp 在時間軸上沒有厚度，若直接拿判定時間去擺，整段音符會疊成一坨。
所以 `SmChart` 把整段被跳過的拍數壓成一個 **1ms 的超高速捲動窗**
（`SmChart.WarpDisplayMs`，窗尾對齊 `warp.TimeMs`）：

* 窗內送一個 timing point，`beatLength = 1ms ÷ 被跳過的拍數` → 那 1ms 內捲動剛好前進「被跳過的拍數」
* warp 內的音符 `ScrollTimeMs` 依拍數等比落在窗裡 → 進場時的間距**就是**正常的一拍
* 一幀約 16 ms，播放頭幾乎不可能停在那 1 ms 裡 → 看起來就是瞬間跳過

`120 BPM`、跳 8 拍的例子（`vBase` 208 px/s，一拍 104 px）：

```
now=1000.0ms : [R  207.8] [F  311.8] [F  415.8] … [F  935.8] [R 1039.8]   ← 照拍子等距排開
now=2000.0ms : [R -832.0] [F -728.0] [F -624.0] … [F -104.0] [R    0.0]   ← 整批瞬間刷過（832px = 8 拍）
```

長條的尾端另有 `ScrollEndTimeMs`（頭在 warp 外、尾在 warp 內時兩者不同）。

## 4. 負 BPM 中間的停拍 = 定格

gimmick 譜最常見的寫法是「一連串 4 拍負 / 4 拍正，每個接縫上放一個停拍」——
`engine[Blue]` 的 `#BPMS ...,204.667=-174,208.667=174,...` 配 `#STOPS ...,204.668=0.230,...`。
StepMania 走到停拍那一拍會**定格**（`GetBeatAndBPSFromElapsedTime` 的 `bFreezeOut`），
玩家看得到「停住的那一瞬間」；停完才一口氣跳過下一段拍子。畫面上就是一格一格的定格動畫，
而不是整段 gimmick 都空白、只有最後轉回正 BPM 才看得到東西。

一段負 BPM 被停拍切成兩段 warp，中間夾一段真實流逝的時間：

| 歌曲時間 | 畫面 |
|---|---|
| `T` | warp 1 刷過去 —— 播放頭落在停拍那一拍 |
| `[T, T + 停拍長度)` | **定格**：那一拍停在判定線上，被 warp 2 掃掉的拍子照拍距排在上方，完全不動 |
| `T + 停拍長度` | warp 2 刷過去 |

實作在 `SmChart.Timeline.DisplayStops()`（送給 `ManiaScroll` 的零速度窗），兩件事少一件畫面就是空白：

* **凍結窗要讓出 warp 的顯示窗。** 那 1ms 是「整段被跳過的拍子瞬間刷過畫面」用的超高速捲動；
  被零速度窗蓋住的話那段捲動就沒了，窗內的音符全部疊在判定線上（`PixelDistance` 恆為 0）——
  定格的那 0.23 秒畫面上什麼都沒有。
* **停拍那一拍的顯示時刻要對齊 warp 的落地時刻**（`warp.TimeMs`），而不是 `RawMs`。
  譜面把停拍寫在負 BPM 段起拍**之後**零點幾拍（作者要的是「同時」，那零點幾拍只是為了讓
  StepMania 認定停拍落在負段裡），於是那零點幾拍的負 BPM 讓 `RawMs` 比落地時刻早零點幾 ms；
  照 `RawMs` 擺，這一拍的音符會掉進**上一段** warp 的超高速窗裡，定格時被甩到判定線下方好幾拍。

同理，接縫處那兩段時刻相同的 warp **不合併**（`Timeline.Close`）：併了之後接縫那一拍會變成
warp 的內部 → 被誤標成打不到的裝飾音，而 StepMania 是打得到的（定格時播放頭就停在那裡）。

warp **內部**（不在頭尾那一拍上）的停拍不定格：那段拍子播放頭是瞬間跳過的，StepMania 也只是把它的
秒數扣掉（負段算出來的 `fFreezeStartSecond` 是負的 → 定格條件不成立），效果只是讓 warp 晚一點落地。

## 5. 不算進 note 總數

warp 內的音符標成 `OsuHitObject.IsFake`：

* **不判定**（`NearestHittable` / `AutoMiss` / `AutoPlay` / `TickBombs` 全部跳過）
* **不進滿分分母**（`OsuBeatmap.TotalNotes`）、不進難度計算（`ManiaStarRating` / `ManiaMsd`）
* **不發打拍音**（`AssistTick.HasTick`）
* 只照 `ScrollTimeMs` 捲過畫面，流出畫面就收掉

StepMania 3.9 其實**會**把它們算進 note 總數（`Player` 的 miss 邏輯照跑），
但那一段播放頭是零秒跳過的，玩家連一幀機會都沒有 —— 等於白扣分。這裡刻意不算（使用者定奪）。

選歌畫面的 note 數走 `SmChart.PlayableNoteCount()`，同樣扣掉 warp 內的音符；
沒有負 BPM 的譜直接走原本便宜的 `NoteCount(noteData)`，行為與輸出完全不變。

## 6. 邊界情形

| 情形 | 處理 |
|------|------|
| `#BPMS:0=-200,...` 開頭就負 | 表頭 BPM（判定窗換算、選歌顯示）取 `FirstPositiveBpm` |
| `#BPMS` 值為 0 | 丟掉（除以 0 沒意義） |
| 負 BPM 段中間夾 `#STOPS` | stop 把時間往上跳、結束這一段 warp → 定格 → 下一段 warp（§4） |
| warp 內部的 `#STOPS` | 不定格，只是讓 warp 晚一點落地（§4） |
| warp 內的 mine | 一樣是 `IsFake` → 不會爆 |
| 兩個 warp 時刻靠得很近 | 顯示窗的 ε 縮到間距的一半，窗不會重疊（重疊會讓 timing point 時間倒退） |
| 沒有負 BPM 的譜 | 完全不走這條路：timing points、音符時間、`ScrollTimeMs == StartTimeMs` 都與改動前逐位相同 |

## 相關

* [scroll-timing.md](scroll-timing.md)
* [scroll-base-bpm.md](scroll-base-bpm.md) — 基準速度怎麼挑（warp 那段極短，不會被選為基準）
* [../reference/sm-yhaniki-notes.md](../reference/sm-yhaniki-notes.md)
