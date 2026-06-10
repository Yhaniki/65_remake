# 音樂與 BGM（Audio & BGM）

> 優先級：**P0 MVP**（至少 1 首測試曲）
> 使用此 system 的 screen：[05-game-arena](../screens/05-game-arena/spec.md), [04-room](../screens/04-room/spec.md)

## 職責

- 歌曲載入與播放
- BGM 管理（大廳、房間背景音樂）
- 音效（判定 hit 音、Miss 音）

## MVP 規格

### 歌曲

- MVP：3～5 首測試用歌曲
- 格式：MP3 / OGG（待確認）
- 每首歌配套面文件（見 [scoring-judgment.md](./scoring-judgment.md)）

### 歌曲資料（草案）

```
Song {
  id: string
  title: string
  artist: string
  duration: number      // 秒
  bpm: number
  chartFile: string     // 譜面文件路徑
  previewStart: number  // 試聽起始秒數
}
```

### 音效

| 事件 | 音效 |
|------|------|
| Perfect | hit_perfect.wav |
| Great | hit_great.wav |
| Good | hit_good.wav |
| Miss | hit_miss.wav |
| 歌曲開始 | 歌曲本身 |
| 結算 | result_fanfare.wav（可選） |

### BGM

- 大廳：1 首循環 BGM（P1）
- 房間：可選 BGM 或靜音
- MVP 可全部靜音，只做遊戲內歌曲

## 改版 / 優化

| 項目 | MVP | 之後 |
|------|-----|------|
| 曲庫 | 3～5 首測試 | 擴充 |
| 大廳 BGM | 可選 | P1 |
| 版權 | 測試用免版權音樂 | 正式授權 |
| 試聽 | 房間選曲時可試聽 | P1 |

## 待確認

- [ ] 歌曲版權怎麼處理？
- [ ] 原版有哪些經典曲目？
