# Scroll 方向

> 與 **自由 / 普通** 正交；任意模式可搭配任一方向  
> 父文件：[../spec.md](../spec.md)

## 概述

| 方向 | 說明 | Phase 1 | MVP P0 | P1 |
|------|------|---------|--------|-----|
| **向上** | 音符自下往上 scroll | ✓ | ✓ | ✓ |
| **向下** | 音符自上往下 scroll | — | — | ✓ |
| **傾斜** | 4 軌道斜向或旋轉 | — | — | ✓ |

## 向上

- 音符自下往上 scroll
- Phase 1 / MVP P0 唯一實作方向
- Ruleset 預設 `ScrollDirection = Up`

## 向下

- 音符自上往下 scroll
- SM `note(RV)` / YHANIKI Default Scroll Reverse 參考
- Phase 1 / MVP P0 **不做**

### 待填

- [ ] scroll 渲染與 up 共用 Ruleset 參數
- [ ] skin `note(RV)` 路徑

## 傾斜

- 4 軌道斜向或旋轉 scroll
- Phase 1 / MVP P0 **不做**

### 待填

- [ ] 原版傾斜視覺考據
- [ ] 軌道 layout

## 實作備註

- 方向為 Ruleset / Skin 參數，判定與計分邏輯不變
- 房間設定：`mode`（free / normal）+ `scrollDirection`（up / down / tilt）

## 相關

- [free-mode.md](./free-mode.md)
- [normal-mode.md](./normal-mode.md)
- [sm-yhaniki-notes.md](../../../reference/sm-yhaniki-notes.md)
