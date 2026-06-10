# SM-YHANIKI 對照筆記

> 來源：[github.com/Yhaniki/SM-YHANIKI](https://github.com/Yhaniki/SM-YHANIKI)（StepMania 3.9 永和金城武版）

## 與本專案對照

| YHANIKI 功能 | 本專案 | 階段 |
|--------------|--------|------|
| 大廳顯示**總按鍵數**（非僅 Tap+Hold 段數） | `Chart.totalNotes` = Tap + HoldHead + HoldRelease | Phase 1 選歌預覽 |
| 連線確認譜面按鍵數相同 | FishNet 開局前 `totalNotes` + hash | MVP |
| `/share` / `/sharefull` 缺歌傳檔 | Steam P2P 或 PlayFab CDN | post-MVP |
| `Default Scroll Reverse` | scroll 方向：向上/向下 | MVP |
| `note(RV)` 逆流 NoteSkin | `Skins/{name}/notes/` + scroll Down | MVP |
| note 流速不受歌曲倍速影響 | scroll BPM 與 audio rate 分離 | MVP |
| hold 結尾 TNS_MARVELOUS 白光 | [skin-system.md](../architecture/skin-system.md) release 特效 | MVP+ |
| 作譜 ddrtime 編輯器 | 不移植；譜面用 osu/SM/GN import | — |
| F5 快速重讀歌曲 | 開發用 hot reload | 可選 |
| TODO: hold 結尾判定模式 | [scoring-hybrid.md](../architecture/scoring-hybrid.md) 頭端合併 | Phase 1 |

## 連線模式參考（MVP 大廳/房間）

- `/host` 換房主 → [room-matchmaking.md](../systems/room-matchmaking.md)
- 大廳 F8 玩家狀態列 → 大廳 UI P1
- 不關門模式 → 房間常開

## 譜面

- `.sm` `#NOTES` 格式 → [SM_GN_NOTE_FORMAT.md](../reverse-engineering/SM_GN_NOTE_FORMAT.md)
- import：`tools/converters/sm_to_chart`

## 相關

- [external-references.md](external-references.md)
- [architecture/skin-system.md](../architecture/skin-system.md)
