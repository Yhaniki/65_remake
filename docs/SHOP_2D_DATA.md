# 商城「非衣服商品」用到的資料檔 (2D 道具 / 藥水 / 特效卡 / 寵物 / 禮包)

這份是給**未來整合**用的清單：把這個 worktree 用到的資料檔記下來，之後才抽得出一份乾淨的 DATA。

## 1. 這個 worktree 的 data root

離線 `sdox_offline/Extracted` **沒有**商城 2D 商品需要的資料夾 (ITEM2D_PACK / DAOJU / PETAVATAR / MATCHITEMS)，
所以 data root 指向線上客戶端的 `assets/閉撰敃氪/DatasSDO`(較完整)，缺的音效/歌曲再從離線樹補。

用 `tools/link_data_root.ps1` 組出來 (全 junction/hardlink，不複製；產物 `DATA/` + `DATA_MANIFEST.csv` 都不入庫)：

| DATA/ 底下 | 來源 | 為什麼 |
|---|---|---|
| 除 UI 外所有頂層資料夾 | `閉撰敃氪/DatasSDO/*` | 線上樹較完整 (多了 ITEM2D / DAOJU / PETAVATAR / PETDANCE / LOBBYSEL / PLANT…) |
| `UI/<每個子資料夾>` | 線上優先，離線補缺 | UI 要「合併」而不是整包換掉，離線獨有的 UI 子資料夾才不會消失 |
| `SE` | `閉撰敃氪/SE` | 356 檔，是離線 87 檔的嚴格超集 |
| `MUSIC` | `sdox_offline/music` | 匯入的 1516 首新歌在離線樹，線上樹沒有 |
| `BGM` | `sdox_offline/Extracted/UI/BGM` | 大廳/房間隨機播放清單 |
| `PROFILE` | 離線樹**實體複製** | 玩家存檔要可寫，不能跟主 worktree 共用 |
| `iteminfo.dat` / `setinfo.dat` | `閉撰敃氪/` 根目錄 | 商品目錄 (31,563 筆) / 套裝組件表 |

`data_root.txt`(已 gitignore) 就是 `SdoExtracted.ConfiguredRoot` 的覆寫入口。

## 2. 非衣服商品實際會讀的檔

**索引 / 目錄**

| 檔案 | 用途 | 讀取者 |
|---|---|---|
| `iteminfo.dat` | 商品 id / modelId / category / 價格 / 名字 / 時效 | `IteminfoReader` → `AvatarItemCatalog` |
| `DRESS.TXT` | **modelId → 資源檔名** 的唯一對照表 (23,020 行) | `DressTable` / `DressCatalog` |
| `PETDRESS.TXT` | 寵物部件 (臉/衣/頭飾/食物) 的資源檔名 | 同上 (疊在 DRESS 之上) |

> ⚠️ 資源檔名帶漢語拼音 (`100000 → 100000_xiaolaba.an`)，**不能**用 modelId 去猜檔名 —— 官方客戶端也是查這張表。

**2D 圖示** (`DRESS` 檔名以 `.an` 結尾 → 圖示；`.an` 內容是「圖集 + 裁切框」，如 `daoju_a.png (360,400,90,100)`)

| 資料夾 | 內容 |
|---|---|
| `UI/ITEM2D_PACK_IN_SHOP/` | 商城限定圖 (背景卡 2xxxxx 在商城用這套) |
| `UI/ITEM2D_PACK/` | 一般道具/藥水/背包圖 (覆蓋率最高) |
| `UI/ITEM2D_PACKUSE/` | 使用中/他人身上 (目前未用，保留在解析順序裡) |
| `UI/MATCHITEMS/` | 寵物圖 (`1000001 → 1000001.an`) |

`Datas/ITEM2D/` **不是**商品圖來源 (離線 exe 裡根本沒有這個路徑字串；那是信紙底圖與星座卡)。

**3D 模型** (`DRESS` 檔名以 `.msh` 結尾)

| 資料夾 | 內容 |
|---|---|
| `DAOJU/` | 道具/禮盒 mesh + DDS (禮包借用 `100400_lihe.msh` 等) |
| `PETAVATAR/` | 寵物本體、寵物頭飾 (`1030xxx_ALL_HEADEAR.MSH`)、寵物衣服共用的 `1040000_all_coat_.msh` + 各件印花 DDS |

**UI 美術**：`UI/SHOP/`(SHOP.XML + ShopBtn 圖集)，新用到的分頁 art：
`Shop105–110`(功能道具/藥水)、`Shop117–119`(人物特效)、`Shop209–211`(動作卡，離線無資料)、
`Shop190–198`(寵物/寵物衣/寵物頭飾)、`Shop36–38`(寵物道具)、`Shop87–89`(礼包)、`Shop33/34/84/85/133/134/136/137`(专卖店促銷分頁，離線無資料)。

## 3. 實測覆蓋率 (以本 worktree 的 DATA 解析 380 個非衣服 modelId)

| category | 分頁 | 件數 | 解得到美術 | 畫法 |
|---|---|---:|---:|---|
| 21000 | 道具店 · 功能道具 | 188 | 181 (96%) | 2D 圖示 |
| 22000 | 道具店 · 藥水 | 21 | 21 (100%) | 2D (DRESS 直接指到變形水/換膚水的美術) |
| 24000 | 道具店 · 人物特效 | 36 | 36 (100%) | 2D (CHARBACK 背景卡) |
| 25000 | 道具店 · 動作卡 | 0 | — | 離線 iteminfo 無此類 (伺服器上架) |
| 41000 | 伙伴店 · 宠物 | 52 | 29 (55%) | 2D 圖示 (MATCHITEMS)；23 隻舊寵物不在 DRESS.TXT |
| 43000 | 伙伴店 · 宠物衣服 | 24 | 20 (83%) | 3D：共用 coat mesh + 各自印花 DDS |
| 42000 | 伙伴店 · 宠物头饰 | 21 | 19 (90%) | 3D：`PETAVATAR/*_HEADEAR.MSH` |
| 44000 | 伙伴店 · 宠物道具 | 10 | 10 (100%) | 2D 圖示 (餅乾…) |
| 14000 | 礼包店 / 专卖店·礼包 | 28 | 28 (100%) | 3D：按 modelId 區間借禮盒模型 (官方寫死) |
| **合計** | | **380** | **344 (90%)** | |

解不到美術的商品**仍然上架**(使用者要求「都上架」)，只是格子沒有縮圖。

## 4. 逆向出處 (file:line)

- category 是 **modelId 區間硬編**、不是資料驅動：`H:\sdo_cn\sdo.bin.c:642996 FUN_00962590`
- 分頁 → category code：`sdo.bin.c:517331 FUN_007ad450`(子分頁按鈕)、`:518231 FUN_007ae4c0`(9 個分頁 CheckBox)
- 官方 6 個分頁裡 **礼包其實是「专卖店」的 package 子分頁**(code 14000)；重製版兩邊都給同一份清單
- `DRESS.TXT` 讀檔：`assets/sdo_stand_alone.exe.c:122092`(`sscanf("%d %s")`)；查表：`:44360 FUN_004312e0`
- 商品格 2D/3D 分流：`sdo_stand_alone.exe.c:44412 FUN_004313c0`；禮包借禮盒模型的寫死區間：`:44586-44637`
- 寵物頭飾/衣服才會試穿到寵物身上：`sdo.bin.c:508120`(`cat<21000 || cat==42000 || cat==43000`)

## 5. 抽乾淨資料包的方法

`SdoExtracted` 已有載入追蹤：設環境變數 `SDO_TRACE_LOADS=<檔案>` 後跑遊戲，所有嘗試載入的貼圖路徑都會寫進去；
搭配 `tools/collect_used_assets.ps1` / `tools/prune_dead_data.ps1` 可以把沒被讀到的檔剔掉。本功能新增的讀取點
(`DressCatalog` 的 DRESS.TXT/PETDRESS.TXT、`Item2dArt` 的 .an、卡片的 DAOJU/PETAVATAR mesh) 都走
`SdoExtracted.LoadAn1` / `SdoAvatarBuilder.ResolveAvatarFile`，所以會被同一套追蹤記錄到。
