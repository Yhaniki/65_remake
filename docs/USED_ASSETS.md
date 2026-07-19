# 素材使用地圖（哪些有用到 / 哪些是重複垃圾）

> 由程式碼靜態追蹤 loader 的解析路徑得出，非猜測。來源檔：
> `SdoExtracted.cs`、`SdoAvatarBuilder.cs`（`ResolveAvatarFile` → `assets/Datas` fallback）、
> `AvatarItemCatalog.cs`（`AvatarDirs`/`ResolveIteminfoPath`）、各 `*Art.cs`（掃 `assets/*/DatasSDO/UI/...`）、
> `tools/package_build.ps1`（打包時實際複製的子樹）。

## 一句話結論

`assets/` 下其實有 **三棵大素材樹**，程式各取一小塊：

| 樹 | 大小 | 程式怎麼用 |
|---|---|---|
| `sdox_offline/Extracted` | 1.6 GB / 33.7k | **Root**，整棵用（MOTION/DANCE/SCENE/3DEFT/CAMERA/NOTEIMAGE/EFFECT/UI…全部從這裡讀）|
| `Datas` | 6.8 GB / 145k | **只用 `Datas/AVATAR`**（4 GB，商城全套服裝 mesh+貼圖，靠 `ResolveAvatarFile` 的 dev fallback）|
| `閉撰敃氪/DatasSDO` | 6.8 GB / 144k | **只用 9 個 `UI/*` 子夾 + `LOADING`**（~397 MB，UI 美術「線上版」覆蓋）|

**`Datas` 與 `閉撰敃氪/DatasSDO` 是 byte-identical 的雙胞胎**（同一份 MD5）。兩棵合計 13.6 GB，實際只用到約 **4.4 GB**（`Datas/AVATAR` + `DatasSDO` 的 UI/LOADING），其餘 **~9.2 GB 是純重複/沒被任何 loader 碰到**。

---

## 有用到的完整清單（相對 `assets/`）

### A. Extracted（Root，整棵）
- `sdox_offline/Extracted/**` — 1.6 GB

### B. 音效 / 歌曲（打包成 DATA/SE、BGM、MUSIC）
- `sdox_offline/SE/**` — 52 MB
- `sdox_offline/music/**` — 8.3 GB ← 體積大戶，但確實有用（歌譜+音樂）
- 大廳 BGM（8 首 ogg）來源是 `Extracted/UI/BGM`（已含在 A 裡），打包後放 `DATA/BGM`
  （`sdox_offline/BGM` 的 BMG_/TEACHING 整包死，不再打包 — 見下方檔案級死檔）

### C. 服裝 mesh 全量目錄（商城/更衣/房間人/舞者的衣物）
- `Datas/AVATAR/**` — 4.0 GB（38,722 個 .MSH；Extracted/AVATAR 只有 120 個基礎體）
  - loader：`SdoAvatarBuilder.ResolveAvatarFile(rel)` → `Root/AVATAR` 找不到才 `assets/Datas/<rel>`
  - **注意**：路徑是寫死的資料夾名 **`Datas`**（不是 `DatasSDO`）。刪掉 `Datas` 會讓編輯器裡的商城服裝變光頭。

### D. 線上 UI 美術（覆蓋 Extracted 的「線上版外觀」）
來源都在 `閉撰敃氪/DatasSDO/`，resolver 掃 `assets/*/DatasSDO/UI/<x>`：
- `閉撰敃氪/DatasSDO/UI/MUSIC/ICONS/**` — 255 MB（選歌封面）
- `閉撰敃氪/DatasSDO/UI/SHOP/**` — 87 MB（商城）
- `閉撰敃氪/DatasSDO/UI/EXPRESSIONS/**` — 21 MB（表情）
- `閉撰敃氪/DatasSDO/UI/BUBBLE2/**` — 6 MB（聊天泡泡）
- `閉撰敃氪/DatasSDO/UI/MYHOUSEDLG/**` — 5 MB（儲物櫃/更衣間）
- `閉撰敃氪/DatasSDO/UI/STATIS/STATISTIC/**` — 5 MB（結算畫面）
- `閉撰敃氪/DatasSDO/UI/ROOMDLG/**` — 5 MB（選歌視窗）
- `閉撰敃氪/DatasSDO/UI/OPTIONDLG/**` — 1.4 MB（設定對話框）
- `閉撰敃氪/DatasSDO/UI/LOBBYDLG/KEYS/**` — 0.1 MB（鍵盤字母圖）
- `閉撰敃氪/DatasSDO/LOADING/**` — 12 MB（進遊戲載入圖）

> `UI/ROOM` 有被 resolver 引用，但 `DatasSDO` 裡沒有這個夾（回退到 `Extracted/UI/ROOM`），所以不列入。

### E. 商城道具資料
- `閉撰敃氪/iteminfo.dat`（單品名/價/modelId）
- `閉撰敃氪/setinfo.dat`（套裝組件）

---

## 沒用到（可安全清掉）

- **`閉撰敃氪/DatasSDO` 除了 D、上述 UI + LOADING 以外全部** — 約 6.4 GB
  （含與 `Datas/AVATAR` 完全重複的 4 GB `AVATAR`，以及 MOTION/AUMOTION/SCENE/3DEFT/CAMERA/PETMOTION… 這些都從 Extracted 讀，不從這裡讀）
- **`Datas` 除了 `AVATAR` 以外全部** — 約 2.8 GB
  （UI/MOTION/AUMOTION/SCENE/3DEFT/CAMERA… 皆重複 Extracted，且無 loader 引用）
- **`sdox_offline` 裡的其它 dump**：`Data`(884M)、`e2`(552M)、`music_offline_bak`(319M)、`UI`(185M)、
  `Datas.sai(.bak)`、反編譯源碼 `src/named/recompile`、安裝殘檔/dll/exe — 皆非 remake 執行期依賴
- **`閉撰敃氪` 裡 DatasSDO/iteminfo/setinfo 以外的**：`Data/`、`BGM/`、`SE/`、dll/exe 等整套線上客戶端殘檔

---

## 檔案級別死檔（已查證，safe 版已刪）

- **SE**：逐檔反查 `Assets/Scripts/**/*.cs`，87 個 `.wav` 中 **48 有引用 / 39 沒引用**。死檔含
  `bingo`、`notbingo`、`SE_0002/0003/0016~0019/0023~0029/0035/0036`、`VOICE_0011/0015/0016/0022~0024/0029`、
  `man_lingdang1~3`、`woman_lingdang1~3`、`countroll`、`fireworks`、`pulldown/pullhove/pullup`、
  `rollflash/rollspeeddown/rollspeedup/rollstop`。（`roll` 本身有用；roll 系列其餘看似變速音效未接。）
- **BGM（`sdox_offline/BGM`，BMG_/TEACHING 31 檔）整包死**：remake 無 `PlayBgm`（`BgmDir` 已移除，本來就沒 consumer）。
  真正在用的前端 BGM = `bgm_000~007.ogg`（8 首，`BgmPlayer` 隨機播）。原本在 SDO 資料的 `UI/BGM`；已把它**移到 `DATA/BGM`**
  （頂層），`SdoExtracted.UiBgmDir` 優先讀 `Root/BGM`、fallback `Root/UI/BGM`（編輯器仍讀未動的 `Extracted/UI/BGM`）。

## 產出：乾淨 ship DATA

`tools/build_clean_data.ps1` → `H:\65_remake_clean\DATA`（13.59 GB / 109,294 檔），打包後 ship 結構：
全部歌曲 + 全 38,724 套服裝(Extracted/AVATAR + Datas/AVATAR 合併) + 內容資料夾整包 + SE 只留 48 個 + lobby BGM 放在 `DATA/BGM`(從 UI/BGM 移出;死掉的 BMG_/TEACHING 不放)。PROFILE 一律「只補缺不覆蓋」——重打包不會蓋掉玩家的存檔/設定;遊戲缺檔時開機會自己生預設檔(ProfileManager 種帳號、RoomConfig 寫範本、DisplaySettingsManager 寫預設 settings.json)。
內容資料夾(MOTION/UI/SCENE/CAMERA/DANCE/AUMOTION)「可能還有死檔但沒刪」——安全起見保留，要精準刪需執行期追蹤。

## 驗證方法（想再確認「有用到」）

1. **靜態**（本檔）：追每個 loader 的 root 解析 + package_build 實際複製清單。
2. **執行期實測**：在 `SdoExtracted.LoadTexture`、`SdoAvatarBuilder.ResolveAvatarFile`、
   scene/motion/audio loader 各埋一行 log 寫檔，跑過房間/選歌/遊戲/商城/更衣/結算，
   收集實際 open 的絕對路徑 → 這是「這一輪玩到的檔」的 ground truth（但無法涵蓋全部歌曲/服裝）。
3. **資料表列舉**（要「檔級」精簡才需要）：解析 `iteminfo.dat` 的 modelId + `DRESS.TXT` 預設 +
   合成 mesh-only id，映射成實際 .MSH 檔名 → 服裝的精確使用集。風險較高，會漏掉靠目錄掃描上架的合成道具。
