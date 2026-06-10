# 個人房間

> 優先級：**P2 post-MVP**  
> 設計方向：[architecture/personal-room-gogh.md](../../architecture/personal-room-gogh.md)（[咕盒 gogh](https://gogh.gg/zh-sc) 風）

## 1. 目的

玩家私人陪伴空間：虛擬形象 + 小場景 + 氛圍 BGM；可展示戰績、可邀友。

- **MVP**：**不做**，大廳入口隱藏

## 2. 原版行為（Reference）

- 獨立 3D 房、家具、好友參觀
- 待考據

## 3. 保留（Keep）

post-MVP 再定。spirit：「我的空間」入口。

## 4. 改版 / 優化（Change）

### 咕盒風方向（post-MVP）

| 元素 | 說明 |
|------|------|
| 虛擬形象 | VMD/MMD idle，非完整 SDO 家具 MMO |
| 小場景 | 1 背景 + 少量擺件 |
| 氛圍 | 環境音、個人 BGM |
| 展示 | 戰績、skin、最近遊玩 |
| 社交 | 好友參觀（P2） |

### 不借 gogh

番茄鐘、桌面寵 AI、付費商城結構。

| 項目 | MVP | 之後 |
|------|-----|------|
| 個人房 | 不做 | post-MVP |
| 入口 | 隱藏 | 大廳「我的空間」 |

## 5. UI 草稿

post-MVP：小窗體感、一角 avatar、柔和背景。參考 gogh 截图。

## 6. 資料與狀態

post-MVP：場景 id、擺件列表、BGM、形象 id。

## 7. 邊界

單人預設；訪客只讀。

## 8. 待確認

- [ ] 與 [07-shop](07-shop/spec.md) 擺件關係
- [ ] 能否當作「練歌」入口（連 Phase 1 選歌）

## 參考

- [external-references.md](../../reference/external-references.md)
- [dance-vmd.md](../../architecture/dance-vmd.md)
