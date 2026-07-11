# Dance Online SDO — 商城 (SHOP) 模式實作參考文件

本文件整合 6 份逆向研究成果（arrowgene 伺服器模擬器原始碼、線上客戶端 `H:/sdo_cn/sdo.bin.c` 反編譯、真實 `iteminfo.dat` 檔案驗證、重製版 Unity 專案現況），作為在 `65/My project` 加入「商城」模式的權威實作依據。

---

## 1. 概觀

官方商城是**線上專屬**功能（離線 standalone build 完全沒有商店/道具/背包邏輯，只有純跳舞；證據：`assets\sdox_offline\sdo_stand_alone.exe.c` 與 `src\` 無任何 iteminfo/shop/price 讀檔，唯一相關字串是貼圖夾路徑 `"Item2D_Pack_In_Shop\\"`）。商城由三塊組成：

1. **客戶端本地道具目錄** `iteminfo.dat`：一個加密二進位檔，**自帶全部道具的 id + 名稱 + 價格 + 分類**。商城櫥窗的名字/圖/價全部來自這裡，**伺服器不下發清單**。
2. **客戶端 UI**：4 個對話框 XML（`ShopDlg\ShopDlg.xml` 等）+ 共用初始化，用 `CtlListCtrl` 顯示商品，雙貨幣欄（遊戲幣 `G_count` / 商城點 `M_count`）。
3. **伺服器交易驗證**：僅用整數 `itemId` 交易，查價、扣款、加背包，回結果碼。arrowgene 模擬器把同一份目錄鏡像進 SQL 表 `ag_item` / `dance_item`。

**移植關鍵坑**：官方協定設計就是「商店櫥窗名稱/價格 100% 本地化在資料檔，伺服器只驗證與扣款」。重製端商城 UI **必須自帶商品表**，不能期待伺服器封包給清單。

---

## 2. 道具資料模型

道具由 `ShopItem` 建模（`ShopItem.java:32-76`）。權威來源是 `iteminfo.dat` 每筆紀錄，也鏡像進 SQL 表 `ag_item`（11 欄，`ag_structure.sql:26-38`）。

### 欄位表

| 欄位 | 型別 | 語意 | 來源 (file:line) |
|---|---|---|---|
| `id` | int32 | 道具唯一 id（= 檔名 6 位前綴） | `ShopItem.java:32` / rec `@0x00` |
| `name` | char[44] NUL 字串 (GBK) | 顯示名稱，內嵌於該筆道具 | `ShopItem.java:33` / rec `@0x14` |
| `price` | int32 | 價格**數值**（不含幣別） | `ShopItem.java:34` / rec `@0x10` |
| `priceCategory` | uint8 | 幣別：POINTS(0)/COINS(1)/BONUS(4) | `ShopItem.java:66` / rec `@0x0C` |
| `modelId` | int32 | 貼圖/模型 id | `ShopItem.java:41` / rec `@0x04` |
| `category` | int32 | 分類魔數（同時編碼性別+部位） | `ShopItem.java:46` / rec `@0x08` |
| `minLevel` | int32 | 可買/可穿最低等級 | `ShopItem.java:51` |
| `duration` | int32 | 租期天數：PERMANENT(-1)/0/7/30 | `ShopItem.java:56` / rec `@0x7C` |
| `quantity` | int32 | 消耗品數量；NOT_CONSUMABLE(-1)=衣服 | `ShopItem.java:61` |
| `sex` | byte | FEMALE(0)/MALE(1)/BOTH(2) | `ShopItem.java:71` |
| `weddingRing` | int16>0→bool | 是否婚戒 | `ShopItem.java:76` |
| `slotType` | **推導，非欄位** | CLOTHES(200) / ITEMS(400) | `ShopItem.java:83-91` |

### 列舉值（整數 → 語意）

- **`ItemCategoryType`**（`ItemCategoryType.java:30-51`，魔數同時編碼性別+部位）：
  - 男：1=髮型 / 2=上衣 / 3=下著 / 4=手套 / 5=鞋 / 6=臉 / 7=眼鏡 / 50=連身 / 201=整套
  - 女：101-107（同上部位）/ 150=連身 / 200=整套
  - 21000=`ITEMS_MAIN_CONSUMABLES`（主要消耗品）/ 24000=`ITEMS_AVATAR_EFFECTS`（角色特效）
- **`ItemPriceCategoryType`**（`ItemPriceCategoryType.java:30-32`）：`POINTS(0)`, `COINS(1)`, `BONUS(4)`（**跳過 2、3**）
- **`ItemDurationType`**（`ItemDurationType.java:30-33`）：`PERMANENT(-1)`, `ZERO(0)`, `SEVEN(7)`, `THIRTY(30)`
- **`ItemQuantityType`**（`ItemQuantityType.java:30-37`）：`NOT_CONSUMABLE_2(-2)`, `NOT_CONSUMABLE(-1)`, 1, 5, 10, 15, 25, 30
- **`ItemSexType`**：`FEMALE(0)`, `MALE(1)`, `BOTH(2)`
- **`InventorySlotType`**（`InventorySlotType.java:30-31`）：`CLOTHES(200)`, `ITEMS(400)`；numValue = 封包槽位基底 id

**`slotType` 推導規則**（`ShopItem.java:83-91`）：`category ∈ {ITEMS_MAIN_CONSUMABLES, ITEMS_AVATAR_EFFECTS}` → `ITEMS`；其餘 → `CLOTHES`。slot 不存於檔案。

**背包實例** `InventoryItem`（`InventoryItem.java:34-40`）= `ShopItem` + `{slotNumber, quantity, expireDate(Unix秒;-1=永久), equipped, characterId}`。封包槽 id = `slotType.getNumValue() + slotNumber`（`InventoryItem.java:62-68`）；`NOT_EQUIPPED_SLOT_ID=0`。消耗品依 `modelId` 堆疊合併（`Inventory.addItem:180-206`）。

---

## 3. 名字清單怎麼定義

**核心事實：道具名稱不是外部字串表、不是 id→名稱查表，而是「直接內嵌在每筆道具的固定 44-byte 欄位」。**

### 3.1 權威來源：`iteminfo.dat`（線上客戶端，加密二進位）

已用倉庫內真實檔 `H:\65_remake\assets\閉撰敃氪\iteminfo.dat` 完整驗證（4,923,841 bytes；headA=2, headB=43515, count=31563；第一筆 `[13457] 黄帽 文静女孩` / cat=101 女髮 / priceCat=1 金幣 / price=1860）。

**檔案總體結構（little-endian）：**

```
Header (12 bytes, 未加密):
  int32 headA        = 2          (版本；務必檢查 ==2)
  int32 headB                     (Java 期望 7008=0x1B60；實檔=43515；區域/版本差異，勿硬檢查)
  int32 headItemCount             (=(fileSize-12)/156，實檔=31563)
之後: headItemCount 筆 × 156 bytes 加密紀錄，檔尾可能多 1 byte padding。
```

**每筆紀錄長度 = 156 bytes (0x9C)，不是 152。** 這是最重要的修正——arrowgene Java 的 `ITEM_LENGTH=152`（`Iteminfo.java:42`）是**錯的**，會在第 2 筆後全部錯位。權威證據：`sdo.bin.c ~638648` `FUN_00a51bfc(&rec, 0x9c, 1, file)`（每筆一次讀 0x9C bytes）。

**解密（逐 byte，自反）：** `dec = (0x1F9 - b) & 0xFF`（即 `(505 - b) mod 256`）。讀寫用同一函式。arrowgene `Iteminfo.java:190-197` 寫成帶符號等價式，結果相同。

**解密後 156-byte 紀錄欄位佈局（offset 已用實檔驗證）：**

```
0x00 int32   id            (13457,13458... 連號)
0x04 int32   modelId
0x08 int32   category      (1=男髮,101=女髮,4=手套...)
0x0C uint8   priceCategory (0/1/4；實檔多為 1)
0x0D uint8   a  (旗標/保留)
0x0E int16   b  (保留)
0x10 int32   price         (1860,2760,490...)
0x14 char[44] name         (NUL 結尾, GBK/CP936 中文)
0x46 char[42] str2         (第二字串欄；實檔全空，疑描述/保留)
0x78 uint16  (=1 常見旗標)
0x7A int16
0x7C int32   duration      (-1永久 / 7七天 / 0 / 30)
0x80~0x9B    minLevel/quantity/sex/weddingRing (實檔多為 0xFF 佔位，offset 未逐一釘死)
```

**編碼陷阱**：名稱是 **GBK/CP936 簡體中文**，NUL 結尾。arrowgene 用 ISO-8859-1 讀（`ByteBuffer.java:289`）只是保留原 bytes，直接當 UTF-8/ASCII 會亂碼。重製端必須用 Encoding 936 解碼。

### 3.2 鏡像來源：SQL 表 `ag_item`（arrowgene 伺服器）

```sql
-- ag_structure.sql:26-38
CREATE TABLE IF NOT EXISTS `ag_item` (
    `item_id` INTEGER PRIMARY KEY,
    `item_name` VARCHAR(255) NOT NULL,
    `item_model_id` INTEGER NOT NULL,
    `item_category` INTEGER NOT NULL,
    `item_min_level` INTEGER NOT NULL,
    `item_duration` INTEGER NOT NULL,
    `item_price` INTEGER NOT NULL,
    `item_quantity` INTEGER NOT NULL,
    `item_sex` INTEGER NOT NULL,
    `item_price_category` INTEGER NOT NULL,
    `item_wedding_ring` BIT NOT NULL
);
```

MariaDB 等價表 `dance_item`（`mariadb_structure.sql:153-168`，短欄名版）。名稱一名一列，非共用字串表。DB→model 回讀見 `SQLiteFactory.java:100-114`。

**已提供的完整目錄種子**（可直接讀取拿全部名稱+價格）：
- `ag_items.sql`（4053 筆 `INSERT INTO ag_item`）
- `mariadb_items.sql`（4055 行 `dance_item`；注意用了 SQLite 專屬 `INSERT OR REPLACE` 語法，灌 MariaDB 前需改）

**無任何 CSV/TSV/TXT 版本**：目錄只有二進位 `iteminfo.dat` 與 SQL 種子兩種形式。

**編輯器**（`ItemListEditor.java`）可用 `Iteminfo` 解密載入 → 改 `textFieldName`/`textFieldPrice`/下拉分類 → `Import into Db`（`insertShopItems` 批次寫 `ag_item`）。但 `save-file`/`save-item`/`newItem` 都是空實作（`ItemListEditor.java:187-193`），**無法寫回 .dat**，也沒把編輯值 set 回 ShopItem。

---

## 4. 價錢怎麼設定

**價格拆成兩件事：數值 + 幣別，缺一不可。**

1. **數值** = `price`（int32，單一欄，rec `@0x10` / `ShopItem.java:34`）。可為 0（免費）甚至 -1（未定義/佔位，常見於膚色 avatar 列）。
2. **幣別** = `priceCategory`（`ItemPriceCategoryType.java:30-32`）：
   - `POINTS(0)` — 遊戲內累積點數（遊戲賺的）
   - `COINS(1)` — 儲值現金幣（cash）
   - `BONUS(4)` — 紅利/活動獎勵幣

同一 `price` 數字搭配不同 `priceCategory` 就扣不同錢包（`Character` 三個獨立餘額欄位）。

**扣款/檢查邏輯**（`Shop.java`）：
- `canAfford(character, currency, amount)`（`Shop.java:209-218`）：分別比 `getPoints()/getCoins()/getBonus() >= amount`。
- `spendMoney(character, currency, amount)`（`Shop.java:198-207`）：
  ```java
  if (currency == POINTS)      character.setPoints(getPoints()-amount);
  else if (currency == COINS)  character.setCoins(getCoins()-amount);
  else if (currency == BONUS)  character.setBonus(getBonus()-amount);
  ```
- 買不起訊息 `MSG_YOU_DO_NOT_HAVE_ENOUGH_COINS_POINTS_OR_BONUS_FOR_THIS_ITEM`（`ShopMessages.java:33`）本身就點出三幣別。
- 硬編價：擴充倉庫 `EXPAND_STORAGE_COST` 固定用 POINTS 扣 1000（`Shop.java:46,175-192`）。

**範例列**（`ag_items.sql`；欄序 id,name,model_id,category,min_level,duration,price,quantity,sex,price_category,wedding_ring）：
```
(6,'F. Capris 1161',1161,103,5, 7, 50,-1,0,1,0)   -- 金幣 租7天  50
(7,'F. Capris 1161',1161,103,5,30,100,-1,0,1,0)   -- 金幣 租30天 100
(8,'F. Capris 1161',1161,103,5,-1,200,-1,0,1,0)   -- 金幣 永久   200
(23,'F. Capris 298',298,103,1,-1,10000,-1,0,0,0)  -- 點數 永久 10000
(851,'F. Costume 1271',1271,150,1,-1,2,-1,0,4,0)  -- 紅利 永久   2
(3785,'Athletic Build',100030,21000,1,-1,1000,1,2,0,0) -- 消耗品(體型) 點數
(3801,'Love Flower',1421,4,1,-1,200,1,1,1,1)      -- 婚戒 (尾欄=1)
```

**定價模式**：同一件衣服常拆成 3 列不同 `duration`(7/30/-1) 配不同 `price`，構成「租 7 天 / 租 30 天 / 永久」三段定價。

**UI 顯示規則**：務必「數值 + 幣別」成對呈現，區分現金 COINS / 點數 POINTS / 紅利 BONUS 三種貨幣圖示，別把 price 當單一貨幣。

---

## 5. 商城 UI/介面流程

來源：線上客戶端反編譯 `H:/sdo_cn/sdo.bin.c`。商城 UI 由 4 個對話框 XML 驅動，全在 `Datas\ShopDlg\`（UI.bin 打包）：

| XML | 用途 | 建構函式 |
|---|---|---|
| `ShopDlg\ShopDlg.xml` | 一般商店主畫面 | `FUN_00804e90` (`:562764`) |
| `ShopDlg\ShopPackDlg.xml` | 套裝/禮包 | `FUN_008056f0` (`:563245`) |
| `ShopDlg\ShopGiftDlg.xml` | 送禮 | `FUN_00806130` (`:563787`) |
| `ShopDlg\ShopPackGiftDlg.xml` | 套裝送禮 | (`:564234`) |

### 共用初始化 `FUN_00804330`（`:562262`，每個對話框載完 XML 都呼叫）

逐字取得 widget（可直接對映到 remake 的 widget 名以保持一致）：
- `"ItemName"` → 品名文字（this+0x134）
- `"CurPrice"` → 目前單價文字（this+0x138）
- `"G_count"` → 遊戲幣(Gold)餘額（this+0x140，值來自 `*(DAT_00c9f594+0x10)+300`）
- `"M_count"` → 商城點/現金點餘額（this+0x144）
- `"cancel"`（+0x10c）、`"close"`（+0x110）兩關閉鈕

→ **貨幣顯示是雙欄**：`G_count`(遊戲幣) + `M_count`(商城點/現金)。付款幣別另由 `payment`/`curpayment`/`payment_list` 三控件選擇。

### 商品清單（`CtlListCtrl`）

資料來自螢幕物件 `this[0x57]` 商品表，每筆 stride=0x24 (36 bytes)：
- `+0xc` 單價/幣別欄（填進 `price` 控件 this[0x3e] 的 list `*(...+0x378)[i]`）
- `+0xc4` 擁有/狀態旗標（0/1，決定名稱樣板）
- `+0xc8` 商品名稱字串指標
- `+0xd0` 商品 id（=0xffffffff 視為空格）

主商店還有 `ShoppingMap`（分頁/分類地圖，this[0x41]）與 `AvatarItem`（造型商品清單）。商品縮圖走 `Items\Item%d.png`（`:701713`）與 `gameplay\PlayItem\item\Item%d.pn`（`:8613`），以商品 id 命名。

### 流程

開啟商城 → 載入對應 `ShopDlg\*.xml` → `FUN_00804330` 綁定品名/單價/雙貨幣欄+關閉鈕 → 用 `this[0x57]` 表填 `CtlListCtrl`（名稱/擁有旗標/價/id）並更新縮圖 → 點商品時 `CurPrice`/`ItemName` 更新 → 按買：
- 一般模式（this+0x154==0）：彈確認框 `DAT_00adfea8` → 確認回呼 `FUN_00802c80` 送 CMsg **0x138a**（選購請求，寫商品 id + 送禮旗標）
- 直購模式（this+0x154==1）：送 CMsg **0x1398**（確認/直接購買）
- 伺服器回 **0x1389** → `FUN_00964ee0`（`:15408`）處理購買結果

送禮版（`ShopGiftDlg`/`ShopPackGiftDlg`）另填 `PlayerName`(收禮人)/`Title`/`Content`(留言)，並用 `Friend`/`Friend_List`/`FriendList` 選好友；送禮旗標寫進 0x138a 封包第 2 byte。

**widget 命名對照（可直接照抄到 remake）**：`ItemName`/`CurPrice`/`G_count`/`M_count`；`cancel`/`close`；`ShoppingMap`/`AvatarItem`/`price`/`payment`/`curpayment`/`payment_list`；送禮 `PlayerName`/`Title`/`Content`/`Friend`。

---

## 6. 封包/流程（arrowgene 伺服器）

opcode 十進位 (十六進位)。**注意：進場封包不含商品清單**（唯一送名稱上線的是背包清單 7001）。

| 動作 | REQ | RES | 說明 |
|---|---|---|---|
| 進商店 | 5012 (0x1394) | 5013 (0x1395) | **無清單**，只回 5 個硬編碼佔位常數（見下） |
| 購買 | 5002 (0x138A) | 5003 (0x138B) | REQ: int32 itemId；RES: int32 resultCode + byte 0 |
| 刪除 | 5004 (0x138C) | 5005 | REQ: int16 slotId；刪後**主動回送 7001 整份背包** |
| 垃圾桶 | 5006 (0x138E) | 5007 | 不真刪，只回確認：int32 0 + string 名 + byte 0 |
| 送禮 | 5008 (0x1390) | 5009 | REQ: string receiver/title/message + int32 itemId（付款 // TODO 未實作） |
| 讀背包清單 | 7000 (0x1B58) | 7001 (0x1B59) | **唯一送物品名稱的封包**（送背包，非櫥窗） |
| 穿脫 | 7002 (0x1B5A) | 7003 (0x1B5B) | REQ: int16 itemSlotId；RES: int32 DressItemMsg + int32 0 |
| 擴充倉庫 | 7008 (0x1B60) | 7009 | REQ: int32 slotType(200/400)；花 1000 POINTS |

**5013 進場回應**（全硬編碼佔位，原碼註解 `// TODO Fehlerhaft`，勿當有意義清單長度）：
```
addInt32(0x1b60); addInt32(0xfd7); addInt32(0); addInt32(0x1f); addByte(0);
```

**5002 購買** → `Shop.buyItem`（`Shop.java:120-146`）：hasSpace → canAfford → spendMoney(依 priceCategory 扣款+寫 DB) → craftItem(依 duration 算到期：PERMANENT→-1, ZERO→1, SEVEN/THIRTY→now+天數秒) → `inventory.addItem`(分配 slotNumber；消耗品同 modelId 疊加)。**買成功不回背包清單**，客戶端需另送 7000 刷新。

**7001 背包清單 wire format**（`ItemPacket.java:48-61`，唯一帶名稱）：
```
int32 count
每筆: int16 slotId(200+n衣服 / 400+n道具) | int32 modelId | int32 quantity
      | byte 0 | string expireDate("yyyy-MM-dd" 或 "Permanent") | byte 1
      | string name(=shopItem.getName()) | int32 minLevel
byte 0  // 結尾
```

**7002 穿脫** check()（`_7002...java:81-114`）：婚戒需符婚姻紀錄+戒指 id；`minLevel > 等級` → `SORRY_YOU_MUST_MEET_THE_MINIMUM_LEVEL`；性別不符 → `CANT_EQUIP_ITEM_WITH_CURRENT_GENDER`。equipItem 依 category 把 modelId 寫入 character 對應欄位(hair/top/pants/…)。

**結果碼**：`ShopMessages`（`ShopMessages.java:30-36`）`MSG_NO_ERROR=0`, `...ENOUGH...=0xfffffffd`, `NO_MORE_ROOM=0xfffffffc`。`DressItemMsg`（`DressItemMsg.java:30-37`）`NO_ERROR=0`, `MIN_LEVEL=0xfffffffe`, `WRONG_GENDER=0xfffffffb`。

**移植提醒**：櫥窗名/價本地化在資料檔（伺服器不下發）；買成功靠客戶端主動送 7000 刷新（5003 不回），但刪除(5004)主動回 7001；slotId 編碼 衣服 200+n / 道具 400+n；到期日以秒存、封包送字串；消耗品用 modelId 疊加而非新格。

---

## 7. 重製整合建議

### 7.1 現況（重製版 Unity）

- **服裝目前無商城概念層**：服裝 = 一組硬編 `.MSH` 檔名字串陣列，**兩處重複**：
  - `ScreenGameplay.cs:47-56` `avatarParts` + `skeletonHrc="AVATAR/FEMALE.HRC"`；`TryLoadAvatar`（`:915-1040`）載入/換皮。
  - `SdoRoomAvatar.cs:15-27` `WomanParts` + `FemaleHrc`；`Build`（`:37-105`）幾乎逐行重複。
- **資料層天生 item-id 化**：`H:\65_remake\assets\Datas\AVATAR\` 有 38,722 個 `{6位id}_{MAN|WOMAN}_{SLOT}.MSH`，6 位前綴=官方 item-id、字尾=slot（HAIR/COAT/PANT/SHOES/ONE/CHIBANG(翅)/DAOJU(道具)/GLASS/RING/TAIL/… + FEMALE/MALE.HRC）。**磁碟無任何 name/price metadata**。
- **渲染管線已完整**：`SdoAvatar.cs`(HRC+MOT+CPU skinning+AddPart) + `MshLoader.cs`(COAT/PANT 兩段材質) + `DdsLoader` + `SdoBodyShape.cs`(體型)。換皮只需換 parts 清單、**不用動 skinning**。
- **無貨幣/背包持久化**：`Sdo.Ruleset\Reward.cs` 算 Coins/EXP 但算完即丟。

### 7.2 資料/邏輯層（純 C#，可單元測試 — 依 MEMORY「all src/ pure logic needs tests」）

**A. 道具目錄** — `Sdo.UI.Catalog.AvatarItemCatalog`（仿 `NoteSkinCatalog.cs`：靜態候選表 + 磁碟過濾）
- 新檔：`65\My project\Assets\Scripts\UI\Catalog\AvatarItemCatalog.cs`
- 定義 `ItemSlot` enum（Face/Hair/Coat/Pant/Shoes/One/Wing/Glass/Ring/Tail/Shoulder/Pate/Hand/Pet… 對應磁碟 SLOT）
- 定義 `AvatarItem { int Id; ItemSlot Slot; bool Male; string NameZh; int Price; PriceCurrency Currency; int Duration; string MshRelPath; }`
- 兩種產生 `Available` 的方式：(a) 掃 `AVATAR/` 依檔名解析 id+slot（名稱只能用 id）；(b) 讀外部 metadata 表補中文名/價格。

**B. metadata 格式** — 建議**兩層**：
- 名稱/價格權威來源：直接寫一個 **`Sdo.ShopItem` C# 解析器解 `iteminfo.dat`**（放 `Assets\Scripts\Sdo.Data\IteminfoReader.cs`），拿 (id, name(GBK), price, priceCategory, category, sex, duration)。**紀錄長度務必 156、名稱用 Encoding 936、只檢查 headA==2**。
- 或直接讀 arrowgene `ag_items.sql` 種子（4053 筆）轉成隨 build 打包的 JSON（仿 `Sdo.Localization StringTable`）。
- 若只需離線示範，也可仿 `NoteSkinCatalog` 手寫靜態候選表（量大不建議）。

**C. 背包/衣櫃/錢包** — 純邏輯資料模型
- 新檔：`Assets\Scripts\Sdo.Shop\Wardrobe.cs`：`{ HashSet<int> Owned; Dictionary<ItemSlot,int> Equipped; int Points; int Coins; int Bonus; }`
- 新檔：`Assets\Scripts\Sdo.Shop\ShopService.cs`：`Buy(itemId)` = canAfford(依 currency 比對餘額) → 扣款 → 加入 Owned；`Equip(itemId)`/`Unequip(slot)`。仿 `Shop.java` 三幣別扣款邏輯，寫單元測試。
- 掛載點：`GameSession.cs`（已放 NoteSkin/Speed/Team 等跨畫面選擇，`GameSession.cs:32-43`）加 `Wardrobe Wardrobe;`，或新立 `IWardrobeService`/`MockWardrobeService`（仿 `Interfaces.cs` 的 `IPlayerService`）。

**D. 裝備→部件解析**（消除兩處硬編漂移，商城單一換裝入口）
- 新檔：`Assets\Scripts\Game\AvatarOutfit.cs`：`ResolveParts(bool male, IReadOnlyDictionary<ItemSlot,int> equipped) → string[]`（回 MSH 相對路徑）。
- **先重構**：把 `ScreenGameplay.TryLoadAvatar`（`:915`）與 `SdoRoomAvatar.Build`（`:37`）幾乎逐行重複的載入迴圈抽成單一 `SdoAvatarBuilder`（吃 parts 清單 + gender），再把兩處硬編 `avatarParts`/`WomanParts` 改成呼叫 `AvatarOutfit.ResolveParts(...)`。這是加商城前的必要前置，否則換裝要改兩處。

### 7.3 UI 層（Screen* 慣例）

**A. `ScreenId` + 轉場表**（`FlowManager.cs:7,15-22`）
- `enum ScreenId { Lobby, Room, SongSelect, Gameplay }` 加 `Shop`；
- `Allowed` 表加邊：`Room` 的集合加 `Shop`、新增 `{ Shop, {Room} }`。

**B. `Nav` hook**（`Nav.cs`）
- 加 `public static Action OpenShop;`（仿現有 `OpenSettings`/`OpenNoteSkinPicker`）。

**C. ShopScreen**
- 選項 1（獨立畫面）：`Assets\Scripts\UI\Screens\ShopScreen.cs : UIScreenBase`（覆寫 Id/BuildUI/OnShow），`FrontendApp.Make<ShopScreen>()` 註冊。
- 選項 2（模態，較貼合官方對話框）：仿 `NoteSkinPicker.cs`（Build/Open/SetVisible + 寫回 session），掛 modalLayer；在 `FrontendApp.cs:82-103` 建模態並註冊 `Nav.OpenShop = () => _shop.Open();`。
- 版面照官方 widget 名：品名 `ItemName`、單價 `CurPrice`、雙貨幣 `G_count`/`M_count`、分類 tab（仿 `ShoppingMap`）、商品清單（仿 `AvatarItem`/`CtlListCtrl`：縮圖+名+價）。縮圖若無 `Item%d.png` 可直接用即時 3D 預覽。

**D. 入口按鈕**
- `RoomScreen.cs:142-148` 頭部 Btn 列已有現成美術 `BtnHeadExchange`（roomexchange，`onClick=null`），直接接 `Nav.OpenShop`。

**E. 試穿即時預覽**
- 房間已有可見本機 3D avatar（`RoomScene3D` + `SdoRoomAvatar`），試穿直接用 `SdoAvatarBuilder` 重建其 parts；或商城自開一個 RT 全身/頭預覽（仿 `RoomHeadPortrait.cs`）。

**F. 持久化**
- 背包/裝備/錢包仿 `Sdo.Settings\RoomConfig.cs` 的「exe 同層 config.ini 讀寫」，或 `JsonUtility` 存檔。

**G. 文字**
- 走 `LocalizationManager`/`StringTable`（JSON entries）加 `shop.*` key（買/取消/餘額不足/租 N 天等）。

---

## 8. 待確認/風險

1. **`iteminfo.dat` 紀錄尾段 offset**：`0x80~0x9B`（minLevel/quantity/sex/weddingRing/slot 確切 offset）在實檔多為 0xFF 佔位，未逐一釘死。需找一份欄位有非佔位值的資料，或對照 `sdo.bin.c:638625-638646` 成員 offset(+0x78,+0x7a,+0x7c,+0x7e..+0x86,+0x92,+0x93) 反推。
2. **`headB` 語意**：Java 期望 7008，倉庫實檔 43515。推測是版本/區服差異，需另一份 `iteminfo.dat` 對照；**移植只檢查 headA==2，勿硬檢查 headB**。
3. **名稱編碼確認**：實檔已驗證是 GBK/CP936 簡中；若移植繁中版需確認來源是否 Big5。
4. **offset 0x46 第二字串欄** 用途未定（實檔全空，疑描述/英文名）。
5. **`slotType` 是否真存於 .dat**：`InventorySlotType.java:27` TODO 自承未確認 200/400 是否出自檔案；目前純由 category 推導。
6. **5013 進場 4 個常數語意**（0x1b60/0xfd7/0/0x1f）原碼標 `// TODO Fehlerhaft`，未解。
7. **商城開啟入口 call site**（哪個大廳按鈕首次 new 商城螢幕）在反編譯中透過 vtable `PTR_LAB_00ad20c4` 函式指標註冊，未定位到單一 call site，需再追螢幕切換分派器（可用 Frida 實機 hook 驗證）。
8. **`ItemPriceCategoryType` 跳過 2、3**（numValue 0/1/4）原因不明，是否有未實作幣別。
9. **arrowgene 已知 bug（移植勿沿用）**：`ITEM_LENGTH=152` 應為 156；`SQLiteShopItem.insertShopItem` 單筆版只綁 10 欄少 wedding_ring；`MariaDbShopItem.deleteShopItem` 誤用 `ag_item`；`mariadb_items.sql` 用 SQLite 專屬 `INSERT OR REPLACE`。
10. **重製版設計決策待定**：(a) 是否支援換性別/男角（MALE 部件與 MALE.HRC 齊備但目前只用 WOMAN 預設，涉及 skeletonHrc 切換）；(b) 中文名/價格用解 `iteminfo.dat` 還是掃檔名+靜態表；(c) 貨幣接 `Reward.Coins`（官方停用只回極少）還是自訂經濟；(d) 是否跨啟動持久化；(e) **先重構 `SdoAvatarBuilder` 再加商城**（強烈建議，避免兩處硬編漂移）。
