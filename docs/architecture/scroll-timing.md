# 滾動與譜面時間軸（Scroll & Timing）

> **原版**：GN / SM / O2Jam 系 — **小節 (measure) + BPM + 拍位置**  
> **重製 Enhanced**：**毫秒時間軸** + osu mania **SV（綠線）** + 使用者 scroll 設定  
> 客户端開關：[game-settings.md](../systems/game-settings.md)

參考：[osu!mania wiki](https://github.com/ppy/osu-wiki/blob/master/wiki/Game_mode/osu!mania/en.md) · [SM_GN_NOTE_FORMAT.md](../reverse-engineering/SM_GN_NOTE_FORMAT.md)

---

## 原版 vs Enhanced

| | 原版 GN/SM | Enhanced（osu 系） |
|---|-----------|-------------------|
| 音符時間 | 小節序 + 拍 + `measure_offset` | **`TimeMs`** |
| 速度 | 全局 BPM + 房間 **SPEED** 倍率 | **SliderMultiplier** + **SV 控制点** + 使用者 `scroll_speed` |
| BPM 變化 | BPM 段改拍長 | Timing control points |
| 局部加速 | 譜面內特殊段（考據） | **SV multiplier（綠線）** |
| 玩家流速 | SPEED 2.5 × 當前 BPM 感 | **定速** 或 **跟 BPM**（設定開關） |

**Classic exe** import GN 時：**小節 → ms** 轉換進 Chart，顯示層可仍模擬舊 SPEED×BPM。  
**Enhanced exe**：全走 osu 模型。

---

## Canonical Chart 時間欄位

```csharp
ChartMeta {
  AudioOffsetMs      // 譜面作者 offset（.osu [General] 负 AudioLeadIn）
  SliderMultiplier   // 基底 scroll（osu）
  TimingPoints[]     // BPM + SV + meter
}

TimingPoint {
  TimeMs: number
  Bpm: number | null           // uninherited BPM
  SliderVelocity: number | null // SV 乘数；null = 继承
  Meter: number | null
}

NoteEvent {
  TimeMs: number               // 判定时刻（音频轴）
  Lane: 0..3
  Kind: Tap | HoldHead | HoldRelease
}
```

Step 1 讀 `.osu` **直接映射**；Phase 1 轉入 Canonical Chart。

---

## GN / SM → 時間（import）

```text
beat = measureIndex + measureOffset + noteFraction
timeMs = TimingPoint.At(beat).BeatToMs(beat) + chart.AudioOffsetMs
```

| 來源 | 轉換 |
|------|------|
| GN `shift` / 小節 | 见 SM_GN `#OFFSET` ↔ shift 公式 |
| SM `#MEASURES` + `#NOTES` | 標準 SM timing |
| 已有 `.osu` | 直接 parse |

import 時產出 **TimingPoints**；BPM 段與 SV 段分離。

---

## 滾動速度合成（Enhanced）

與 osu mania 一致：

```text
pixelPerMs = baseScroll(scroll_speed, scale_with_bpm, currentBpm, sliderMultiplier)
            × activeSvMultiplier(timeMs)
```

| 因子 | 來源 | 儲存 |
|------|------|------|
| `scroll_speed` | 使用者設定 / 房間 SPEED / Ctrl+± | 本地；可選 **Steam 同步** |
| `scale_with_bpm` | [game-settings.md](../systems/game-settings.md) `scale_scroll_with_bpm` | 同上 |
| `sliderMultiplier` | 譜面 [General] | 譜面檔（不同步） |
| `activeSvMultiplier` | 當前時間前最後一個 **SV timing point**（osu 綠線） | 譜面檔（不同步） |

> **使用者設定先寫本地**；設定頁可勾 **同步到 Steam**（MVP+）。見 [game-settings.md § 設定儲存與同步](../systems/game-settings.md#設定儲存與同步)。

### 定速 vs 跟 BPM（osu 同款）

| `scale_scroll_with_bpm` | 行為 | Steam 同步 |
|--------------------------|------|------------|
| **關**（Enhanced 預設） | 同一 `scroll_speed` 在 100 BPM 與 200 BPM **下落像素速度相近**（dominant BPM 正規化，同 osu fixed） | 可勾選 |
| **開** | scroll 隨 **當前 BPM** 縮放（舊 SDO / 早期 osu 感） | 可勾選 |

### 記住個別圖譜流速

| `remember_scroll_per_beatmap` | 行為 | Steam 同步 |
|-------------------------------|------|------------|
| **關**（Enhanced 預設） | 全域一個 `scroll_speed` | 可勾選 |
| **開** | 以 `chartHash` 存 `{chartHash: scroll_speed}`；進該譜自動套用 | per-map 表可勾選 |

---

## SV（綠線）

| 項目 | 說明 |
|------|------|
| 編輯 | osu editor mania **timing → SV**；綠線標記 |
| 檔案 | `.osu` `TimingPoints` 中 ` uninherited = 0` 的 SV 行 |
| Runtime | 影響 **視覺 scroll**，**不改** 判定 `TimeMs` |
| Classic | 可選不渲染 SV 或簡化（待決） |

---

## Global offset（使用者）

與譜面 `AudioOffsetMs` **分開**：

```text
audioClock = trackTime
hitWindowCompare = note.TimeMs + chart.AudioOffsetMs + user.GlobalOffsetMs
```

設定 UI：[game-settings.md](../systems/game-settings.md) 音效分頁。可勾選 **Steam 同步**。

---

## Classic vs Enhanced 流速

| | Classic exe | Enhanced exe |
|---|-------------|--------------|
| `scale_scroll_with_bpm` | **强制 true**（= osu 勾选 BPM scale） | 用户可选；**默认 false**（定速） |
| UI 开关 | **不暴露** | OPTION 显示 |
| `remember_scroll_per_beatmap` | **不暴露** | 用户可选 |
| 房間 SPEED | 侧栏 ±（× BPM） | 绑 `scroll_speed` |
| SV 绿线 | 不暴露给玩家 | 读谱 SV × scroll |

Classic 实现：`ScrollSpeedCalculator.ForClassic()` 内部 `scaleWithBpm: true` 写死。

---

## 房間 SPEED 2.5（原版對照）

原版側欄 SPEED 在 Enhanced 語意映射：

| 原版 | Enhanced |
|------|----------|
| SPEED 2.5 + BPM 50 | `scroll_speed = 2.5` + 若開 BPM scale 则随 BPM |
| 房主調 SPEED | 房主調 **scroll_speed**（普通模式） |
| 自由模式 | 各玩家自調（若允許） |

---

## 程式切塊

```
Remake.Chart/
├── TimingPoint.cs
├── ScrollVelocityResolver.cs    # 給定 timeMs → svMult
└── ScrollSpeedCalculator.cs     # scroll_speed + bpm scale → px/ms

Remake.Ruleset/
└── GameplayClock.cs             # audio + offsets

Remake.Unity.Enhanced/
└── ScrollSpeedController.cs     # F3/F4、remember per map
```

---

## 階段

| 階段 | 範圍 |
|------|------|
| Step 1 | `.osu` 時間 + 固定 scroll；無 SV 編輯 |
| Phase 1 | SV 讀取、定速預設、global offset |
| MVP | 完整 osu 流速設定 + 房間 SPEED UI |
| Classic | GN import + **BPM 制 scroll**（无定速开关） |

---

設定 UI 在 [game-settings.md](game-settings.md)。Steam Cloud 細節見 [online-services.md](online-services.md#steam-cloud-設定同步)。

## 相關

- [chart-format.md](chart-format.md)
- [game-settings.md](../systems/game-settings.md)
- [sm-yhaniki-notes.md](../reference/sm-yhaniki-notes.md) — scroll 不受歌曲 rate 影響
