# 商城 (SHOP) 模式 — 實作進度與後續規格

配合 [`shop-mode-reference.md`](shop-mode-reference.md)（官方逆向結論）。本檔記錄**在 `feat/shop-mode` worktree 已實作/驗證的東西**，以及剩下 `ScreenShop` UI 的實作規格。分支尚未 commit。

---

## 已完成（皆通過編譯 / 單元測試）

### ① 重構：`SdoAvatarBuilder`（消除三處重複）
`Assets/Scripts/Game/SdoAvatarBuilder.cs` — 把原本**三份**幾乎相同的 avatar 部件載入迴圈收斂成一個：
- `ScreenGameplay.TryLoadAvatar`（跳舞者）
- `ScreenGameplay.Hud.BuildIdleHeadAvatar`（結算頭貼，`SkinStyle.Portrait`）
- `SdoRoomAvatar.Build`（大廳）

`LoadParts(parent, avatar, parts, SkinStyle, namePrefix)` 統一處理材質（Unlit/Texture、髮雙面、COAT/PANT 雙材質、PortraitOpaque）。淨 −164 行。**dotnet build Sdo.Game = 0 error**。

新增 `SdoAvatarBuilder.ResolveAvatarFile(rel)`：先找 `Root/AVATAR`，再退回 dev 全套 `assets/Datas/AVATAR`，讓全套 catalog 在編輯器可試穿。

### ② 資料層：`Sdo.Shop`（純 C#，`noEngineReferences`，可單元測試）
`Assets/Scripts/Sdo.Shop/`：
| 檔 | 內容 |
|---|---|
| `ItemTypes.cs` | `ItemPriceCurrency`(Points0/Coins1/Bonus4)、`ItemSex`、`ItemSlotType`(Clothes200/Items400)、`EquipSlot`、`ItemCategory` 常數 + 映射 helper（category→slot/gender/MSH 後綴） |
| `ShopItem.cs` | 目錄項（id/name/price/currency/modelId/category/…）+ 計算屬性 `Currency`/`SlotType`/`EquipSlot`/`MshRelPath` |
| `IteminfoReader.cs` | 解 `iteminfo.dat`：**156-byte** 紀錄、解密 `(0x1F9-b)&0xFF`、headA==2（**不檢查 headB**）、GBK 名（注入 Encoding，預設 Latin1 免依賴） |
| `Wardrobe.cs` | `Wallet`(三幣別) + 擁有(`OwnedItem`,含到期) + 裝備(slot→id) |
| `ShopService.cs` | 忠實移植 `Shop.java`：`Buy`(hasSpace→canAfford→spend→craft到期→own)、`ComputeExpire`、`CanEquip`/`Equip`(minLevel/性別/到期) |

測試 `Assets/Tests/EditMode/{IteminfoReaderTests,ShopServiceTests,ItemMappingTests}.cs` — **29 NUnit 全綠**，且對倉庫內真實 `iteminfo.dat` 端到端驗證：**31,563 筆**、第一筆 `[13457] 黄帽 文静女孩 cat101 price1860 Coins`、GBK 中文名正確。

### ③ 橋接（`Sdo.Game`，已 `dotnet build` 過）
- `AvatarItemCatalog.cs` — 載 `iteminfo.dat`（GBK，找不到 codepage 則 Latin1 退回）→ 過濾出衣物 → 標記可渲染（模型在磁碟）→ 依 (性別, slot) 分組供 UI。
- `AvatarOutfit.cs` — `ResolveParts(equipped)`：把裝備的商品疊在 WOMAN 預設上（Hair→Hair、Top→COAT、OnePiece 取代 Top 去掉 Bottom…），產出 `SdoAvatarBuilder` 要的 parts 清單。
- `SdoRoomAvatar.Build(parent, layer, portraitOpaque, parts=null)` — 加可選 parts 參數，商城試穿用它重建預覽 avatar。
- `FlowManager` 加 `ScreenId.Shop`（Room↔Shop 邊）；`Nav.OpenShop` hook。

---

## 關鍵發現：item id ≠ 模型檔名

**MSH 檔名前綴是 `modelId` 補零到 6 位，不是 item id**（常見誤解）。
- 路徑 = `AVATAR/{modelId:D6}_{MAN|WOMAN}_{SLOT}.MSH`（`ShopItem.MshRelPath`）。
- gender 由 category 區塊（男 1-7,50,201 / 女 101-107,150,200）。
- SLOT 後綴：Hair→`HAIR`、Top→`COAT`、Bottom→`PANT`、Gloves→`HAND`、Shoes→`SHOES`、Face→`FACE`、Glasses→`GLASS`、OnePiece→`ONE`。
- 驗證：31,563 筆中 **30,138（95%）** 的 `MshRelPath` 能在磁碟找到模型。
- ⚠️ **資料落點**：執行時 `SdoExtracted.Root/AVATAR`（Extracted）只有 **120** 個 MSH；全套 38,722 在 `assets/Datas/AVATAR`（dev staging）。打包時要把全套 AVATAR 放進 `DATA/AVATAR`。`ResolveAvatarFile` 已做 Root→Datas 後備。
- `iteminfo.dat` 目前只在 `assets/閉撰敃氪/`（`AvatarItemCatalog` 會掃 `assets/*/iteminfo.dat` 找到）；打包要放進 `DATA`。

---

## `ScreenShop` UI（✅ 已建構 + Sdo.UI 編譯過；視覺版面待 Unity 微調）

`Assets/Scripts/UI/Screens/ShopScreen.cs` — 模態，仿 `NoteSkinPicker` 的 Build/Open/SetVisible。已接線：
- `GameSession.Wardrobe`（新增，`SeedRoomDefaults` 給起始錢包）。
- `FrontendApp` 建立 `_shop` + `Nav.OpenShop = () => _shop.Open()`。
- 房間頭部 `roomexchange` 按鈕（`RoomScreen.cs:145`）接 `Nav.OpenShop`（原本 onClick=null）。
- 內容：性別切換 + 部位 tab（髮/上衣/下著/鞋/手套/眼鏡/連身）、商品清單（名+價+幣別+擁有/穿著/無模型標記）、選中資訊、購買（`ShopService.Buy`）、穿上（`ShopService.Equip` → 重建預覽）、**即時 3D 試穿預覽**（RenderTexture + 專用相機 layer 12 + `SdoRoomAvatar.Build(outfit)`）、關閉。
- `AvatarItemCatalog.ById` 供把裝備 id 解回 `ShopItem` → `AvatarOutfit.ResolveParts`。

**待 Unity 目視微調**：面板/預覽/tab/清單的座標與尺寸是估的（`ShopScreen.Build` 內），開 Unity 跑起來後校位置；預覽相機的距離/高度（`BuildPreview`）也要對人物身高校一下。**先按 `Tools/Shop/Dump Catalog` 確認 GBK 中文名在你 runtime 正常**。以下為原始規格參考：

**建議做法**（仿現有 modal，如 `NoteSkinPicker`；文字用 `TextMesh`/TMP、圖用 `SpriteRenderer`，與 gameplay HUD 一致）：

1. **持有狀態**：在 `GameSession`（`UI/Core/GameSession.cs`）加 `Wardrobe Wardrobe`（單人重製可給初始錢包，仿 `RoomConfig` 存 `config.ini`）。
2. **畫面**：`Assets/Scripts/UI/Screens/ShopScreen.cs`（或 modal），資料來源 `AvatarItemCatalog.Instance`。
   - 分類 tab：性別 × `EquipSlot`（Hair/Top/Bottom/Shoes/Gloves/Glasses/OnePiece），對應官方 `ShoppingMap`。
   - 商品清單：`catalog.Group(sex, slot)` → 每列顯示 `item.Name` + `item.Price` + `item.Currency` 圖示 + 擁有/裝備標記（`IsRenderable` 為 false 者灰顯「無模型」）。對應官方 `ItemName`/`CurPrice`/`CtlListCtrl`。
   - 雙貨幣欄：`Wardrobe.Wallet.Points`/`Coins`/`Bonus`，對應官方 `G_count`/`M_count`。
   - 試穿預覽：把選中/裝備的 `ShopItem` 丟 `AvatarOutfit.ResolveParts(...)` → `SdoRoomAvatar.Build(parent, layer, false, parts)` 重建預覽（房間已有 3D avatar 可仿）。
   - 買：`ShopService.Buy(wardrobe, item, nowUnix)` → 依 `BuyResult` 提示（成功/餘額不足/已擁有）。裝備：`ShopService.Equip(...)` → 重建 avatar。
3. **接線**：`FrontendApp` 建立 ShopScreen 並設 `Nav.OpenShop = () => shop.Open();`；房間頭部按鈕（`RoomScreen` 的 `BtnHeadExchange`/roomexchange，目前 onClick=null）接 `Nav.OpenShop`。`FlowManager` 已允許 Room↔Shop。
4. **中文字型**：走 `LocalizationManager` 的 TMP 動態中文字型（仿排行榜）。

**待決策**：(a) 貨幣要接 `Reward.Coins` 還是自訂經濟；(b) 是否跨啟動持久化衣櫃；(c) 男角支援（MALE 部件齊備，但需切 `skeletonHrc`）；(d) 打包時把全套 AVATAR + iteminfo.dat 納入 DATA。
