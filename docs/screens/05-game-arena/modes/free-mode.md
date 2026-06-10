# 自由模式

> **Phase 1 P0** — Phase 1 固定此模式  
> 父文件：[../spec.md](../spec.md)  
> 計分：[scoring-hybrid.md](../../../architecture/scoring-hybrid.md)  
> 方向：[scroll-directions.md](./scroll-directions.md)

## 模式說明

- **自由模式**：不記勝負；玩家自選難度（Easy / Normal / Hard）
- 方向（向上 / 向下 / 傾斜）獨立定義，與本模式正交組合

## 原版（Reference）

- 練習向，無排名壓力
- **不記** personal best、無回放

## 保留（Keep）

- 4K 方向鍵
- 玩家自選難度

## 改版（Change）

| 項目 | Phase 1 | MVP+ |
|------|---------|------|
| 勝負 | 不記錄 | 不記錄 |
| 計分 | 1e8 hybrid | 同 |
| 難度 UI | 選歌頁選 slot | 房間內各自選（自由） |
| 方向 | 向上 | 三向（P1 向下 / 傾斜） |
| Personal best | — | **本地**（分數、準確度、日期） |
| 回放 | — | **本地 `.rpl`**，重看按鍵 + 舞蹈（[replay-local.md](../../../systems/replay-local.md)） |

## 本地 best 與回放（新版）

原版自由模式不存成績；新版在 **自由模式** 為每首歌 × 難度 slot 記 **本地 best**，並附 osu 風 **輸入回放**（非影片）：

- 存按鍵時間軸 → 播放時 Ruleset **deterministic 重判**
- 舞蹈跟同一時間軸播 chart VMD + 判定驅動反應，不另存骨骼
- 僅本地；不上傳、不進 PlayFab 勝負

**P1** 實作（Phase 1 不做）。入口：結算「觀看回放」、選歌「觀看最佳回放」。

## Phase 1

選歌畫面選難度 → 直接開局；無「普通 / 自由」切換（隱含自由）。  
方向固定 **向上**（見 [scroll-directions.md](./scroll-directions.md)）。

## 操作

↑↓←→（Phase 1 可加 WASD P1）

## 相關

- [replay-local.md](../../../systems/replay-local.md) — 本地回放格式與流程
- [normal-mode.md](./normal-mode.md) — 普通模式（MVP）
- [result-screen.md](../result-screen.md)
