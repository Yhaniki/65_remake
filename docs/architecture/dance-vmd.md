# 舞蹈與 VMD

> Phase 1 **不做**。MVP+ 目標。

## Runtime

- Unity 直接播 **VMD**（bone + camera）
- 不依賴 MOT 作為主路徑（MOT 考據見 [reverse-engineering/](../reverse-engineering/)）

## 能力

| 能力 | 說明 |
|------|------|
| Bone 動畫 | VMD bone keyframes → Unity Humanoid/Generic |
| Camera | VMD camera track；可 director 覆寫 |
| 多人隊形 | `FormationTrack` sidecar 或 Chart 擴充；FishNet 同步 seed |
| Idle | 大廳/個人房待機動作 |

## 格式文件

- [mmd_vmd_format.md](../reverse-engineering/mmd_vmd_format.md)
- [VMD_TO_MOT_GUIDE.md](../reverse-engineering/VMD_TO_MOT_GUIDE.md) — 舊版轉檔參考
- [DPS_FORMAT.md](../reverse-engineering/DPS_FORMAT.md) — 原版 GN 配套動畫

## Phase 1

2D 音符 UI + 靜態背景 placeholder。

## 相關

- [unified-motion-rig.md](./unified-motion-rig.md) — MMD ⇄ 熱舞動作/模型互通設計(canonical rig 中樞)
- [systems/dance-performance.md](../systems/dance-performance.md)
- [personal-room-gogh.md](personal-room-gogh.md)
