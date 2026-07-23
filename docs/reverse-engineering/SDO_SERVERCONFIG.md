# serverconfig（歌單順序 + NEW/HOT/推薦/古典 標籤）

**選歌畫面的「歌曲順序」與「NEW / HOT / 推薦 / 古典」標籤，都不在 `SongList.dat` 裡** —— 兩者都來自
**serverconfig**（`serverconfig.dat` / `ServerConfigND.dat`；[NX]Patch 換成了 `patch Datas\config2`）。

`SongList.dat` 只提供**曲目資料**（檔名、fileId、BPM、三難度等級/音符數/長度、歌名歌手，見
[`SONGLIST_FORMAT.md`](SONGLIST_FORMAT.md)）。serverconfig 提供**哪些歌可見、以什麼順序、掛什麼標籤**。

逆向對象：`sdo.bin`（線上客戶端，`SDO_FanTi.exe` / `DDROnline_D.exe` / `NXPatch.exe` 同一份程式碼，
位址以 sdo.bin ImageBase 0x400000 為準）。

---

## 1. 檔案在哪、長什麼樣

| 檔案 | 說明 |
|------|------|
| `serverconfig.dat` | 隨客戶端安裝的那份，**加密**（見 §2） |
| `ServerConfigND.dat` | 登入時從伺服器下載覆蓋的那份，實測**明碼**（`0x04` 起就是 magic）。`SDO_FanTi.exe` / `NXPatch.exe` 才有這個字串，`sdo.bin` 只有前者 |
| `patch Datas\config2` | **[NX]Patch 的版本**：內容就是 ServerConfigND，外面包一層 patcher 自己的混淆（見 §2.2）。`patch Datas\config1` 同理 = `OPEN_BPM.dat` |

外層版面（`FUN_00431500` 讀檔）：

```
0x00  u32   size（= 檔案長度 − 8）
0x04  ...   內容（必要時解密）：前 16 bytes 必須是 "ServerConfig0073"，不符就整份放棄
```

讀法：`fopen(name,"rb")` → 取檔長 → **跳過前 4 bytes** → 讀 `size−4` bytes → 解密 → 比對 magic。

---

## 2. 加密

### 2.1 官方（`FUN_004214c0`）

8-byte 區塊、兩張金鑰表（`0xb02610` / `0xb02910`，由 `FUN_00420fb0` 建表），由旗標 `[0xb02608]`
決定走「單次」或「三重」。**實測 `ServerConfigND.dat` 是明碼**（檔案 `0x04` 起直接看得到
`ServerConfig0073`），所以要讀伺服器下發的那份不必碰這段；`serverconfig.dat` 才是密文。

### 2.2 [NX]Patch 的混淆（**已完全破解**）

NXPatch 在 `.nxd` 節（VA `0x124f000`）掛了一組 kernel32 hook（CreateFile / ReadFile /
SetFilePointer / CloseHandle），把兩個檔名重導向：

```
OPEN_BPM.dat        ←  patch Datas\config1     （seed 0x5B）
ServerConfigND.dat  ←  patch Datas\config2     （seed 0xC3）
```

ReadFile hook（`0x124f99b`，逐位元組迴圈在 `0x124fa1e`）對讀到的每個 byte 做：

```c
al  = (pos & 0xFF) ^ ((pos >> 8) & 0xFF) ^ SEED;   // pos = 該檔的「檔案位移」，hook 自己記在全域
al  = rol8(al, 3);
al += 0x3D;
buf[i] ^= al;                                      // 對稱，加解密同一式
```

`pos` 由 hook 維護（`[0x1250161]` / `[0x1250169]`），SetFilePointer hook 會同步它，所以
**keystream index 就是檔案位移**，可以純離線解：

```python
def ks(i, seed):                       # seed: config1=0x5B, config2=0xC3
    v = ((i & 0xFF) ^ ((i >> 8) & 0xFF) ^ seed) & 0xFF
    v = ((v << 3) | (v >> 5)) & 0xFF
    return (v + 0x3D) & 0xFF
plain = bytes(b ^ ks(i, seed) for i, b in enumerate(open(path,'rb').read()))
```

驗證：`config2` 解出來 `0x00` = `0x9914`(size)、`0x04` = `ServerConfig0073`，後面每張表都對得起來。
（注意「seed 屬於哪個檔」是看 hook 記住的**檔案 handle**，不是檔名；實測 config1→0x5B、config2→0xC3。）

---

## 3. 內容版面（`FUN_00431500` 逐欄解析）

magic 之後全部是 little-endian，`N` 一律是 u32 前綴：

| 順序 | 內容 | 存進 SongDb |
|------|------|-------------|
| — | 16B magic `ServerConfig0073` | — |
| — | u32（版本/時戳，實測 `0x003333ad`） | `+0x28` |
| 1 | `N` + `N`×u32 歌曲 id | `+0x70` |
| 2 | `N` + `N`×u32 | `+0x16c` |
| 3 | `N` + `N`×u32 | `+0x298` |
| 4 | `N` + `N`×u32 | `+0x304` |
| 5 | `N` + `N`×u32 | `+0x364` |
| 6 | `N` + `N`×u32 | `+0x43c` |
| 7 | `N` + `N`×u32 | `+0x49c` |
| 8 | `N` + `N`×u32 | `+0x40` |
| 9 | **固定 5 組 × 4×u16**（40 bytes，每個 `==1` 存成 bool） | `+0x2c`…`+0x40` |
| 10 | `N` + `N`×**12 bytes** ← **SDO 模式歌曲表** | `+0x7c` |
| 11 | `N` + `N`×12 bytes（AU 模式） | `+0x178` |
| 12 | `N` + `N`×12 bytes（第三模式） | `+0x2a4` |
| 13 | `N` + `N`×8 bytes | `+0x4c` |

### 12-byte 歌曲列（**這就是順序與標籤的來源**）

| 偏移 | 型別 | 意義 |
|------|------|------|
| +0 | u32 | **歌曲 id**（= `fileId % 10000`，例：`sdom0040K.gn` → fileId 10040 → id 40） |
| +4 | u8 | **NEW** |
| +5 | u8 | **HOT** |
| +6 | u8 | **RECOMMEND（推薦）** |
| +7 | u8 | **非 0 = 隱藏／未開放**（此列不套標籤，改丟進另一個清單，選單看不到） |
| +8 | u8 | **CLASSICAL（古典）** |
| +9..11 | — | 填充（實測 `CC CC CC`，未初始化的堆疊值） |

---

## 4. 怎麼跟 SongList.dat / MUSIC.DOM 兜起來

`FUN_00431500` 讀完 serverconfig 後接著呼叫：

| 函式 | 做什麼 |
|------|--------|
| `FUN_0042e400` | 讀 `music\Songlist.dat`（`u16` 筆數 + 每筆 **0x2F4=756** bytes 原封存進 vector `+0x88`），另外還有 `MoveUp` / `Drum` / `Sixkey` / `O2Jam` / `NewLean` 各一份 |
| `FUN_0042e7e0` | 從 `music.dom` 容器複製一份 **0x680 bytes/首**的物件到 vector `+0x184`（AU 模式用；欄位名就叫 `NEW`/`HOT`/`RECOMMEND`，見 §6） |
| `FUN_0042e8a0` | **join**（下詳） |
| `FUN_0042f090` | 依旗標與 BPM 建各分頁清單：NEW(`+0x1c0`) / HOT(`+0x1cc`) / 推薦(`+0x1d8`) / BPM<100…≥160 |

join（`FUN_0042e8a0`）的形狀：

```c
for (row in serverTable[+0x7c])                 // ← 外層是 serverconfig 的表
    for (rec in songlistRecords[+0x88])         // ← 內層掃 SongList.dat
        if (rec.fileId % 10000 == row.id) {
            rec.fileId = row.id;                // 正規化成 4 位數
            if (row[7] == 0) {                  // 沒被隱藏
                rec[0xC4] = row[4];             // NEW
                rec[0xC5] = row[5];             // HOT
                rec[0xC6] = row[6];             // RECOMMEND
                rec[0x1C3] = row[8];            // CLASSICAL
                push(rec → +0x94);              // 可見清單（再依 rec[0x1C4]==1 分 +0xac / +0xb8）
            } else push(rec → +0xa0);           // 隱藏清單
        }
// 同一函式後半對 music.dom 物件做同一件事（表 +0x178 → obj+0x664/0x670/0x674）
```

推得出兩件**對重製版很重要**的事：

1. **顯示順序 = serverconfig 表的排列順序**（外層迴圈），不是 SongList.dat 的順序。
2. **一首歌要出現在選單，必須同時存在於 SongList.dat 與 serverconfig 表**。

> SongList.dat 檔內的 `+0xC4/0xC5/0xC6/0x1C3` 只是預設值（實測 1160 筆全是 `1/0/0/0`），開機後一律被
> serverconfig 覆寫 —— **改 SongList.dat 是改不到標籤的**。

---

## 5. 選單怎麼畫

`MusicSelDlg` 開場時（`0x569a50` 起，AU/JAM 另有兩份）用 `new%d` / `hot%d` / `recommend%d` /
`classical%d` / `musicSel_%d` 把 12 列的控制項指標快取到 this 的 `+0x23c` / `+0x26c` / `+0x29c` /
`+0x2cc` / `+0x2fc`。XML 在 `DATA\UI\ROOMDLG\MUSICSELDLG.XML`：四種標籤的 `Label` 疊在同一個位置
（`new1..12` / `hot1..12` / `classical1..12` / `recommend1..12`），美術是同一張 `MusicSelDlg.png` 的四個裁切：

```
NEW.AN        MusicSelDlg.png (774,40,63,25)
RECOMMEND.AN  MusicSelDlg.png (773,71,63,25)
HOT.AN        MusicSelDlg.png (347,729,62,32)
CLASSICAL.AN  MusicSelDlg.png (426,725,71,25)
```
（離線 Extracted 只有 `NEW.AN`/`NEWSIGN.AN`，另外三個要從線上樹 `DatasSDO\UI\ROOMDLG` 拿。）

畫列的是 `FUN_00566e80`（SDO 模式，直接讀 756-byte 記錄）：

```c
ebx = count - startIndex - 1;        // 從清單「最後一筆」開始
for (12 列) {
    rec = list[ebx];  ebx--;                       // ← 遞減：清單反序顯示，最後一筆在最上面
    name  = rec + 0x234;                           // 歌名
    level = *(u16*)(rec + 0x1DC + diff*2);
    time  = *(u32*)(rec + 0x2D8 + diff*4);         // → "%02d分%02d秒"
    if      (rec[0xC4]) show(new[i]);              // 互斥、依序判斷
    else if (rec[0xC5]) show(hot[i]);
    else if (rec[0xC6]) show(recommend[i]);
    else if (rec[0x1C3]) show(classical[i]);
}
```

AU 模式是 `FUN_00567350`，形狀一樣，只是資料換成 music.dom 物件（`+0x198` 歌名、`+0x4c0` BPM、
`+0x67c` 秒數、`+0x664/0x670/0x674` 三個旗標）。

**優先序：NEW > HOT > 推薦 > 古典**，一列最多一個標籤。

---

## 6. MUSIC.DOM（AU 模式的歌曲表）

`Datas\MUSIC.DOM` 是 `DOM!` 格式的「schema + 資料」表，欄位名直接寫在檔頭：

```
INDEX, SINGER, MUSIC NAME, FILE NAME, START MADI, END DELETE MADI, BPM, OFFSET, USE,
INFO, MONEY, CASH, NEW, CHARGE, TOTAL MADI, HOT, RECOMMEND, BACKUP, MUSICTIME
```

也就是說 **NEW/HOT/RECOMMEND 是這個系列的一等欄位**，SDO 模式沒有這張表，才改用 serverconfig 的 12-byte 表補上。

---

## 7. 實測資料

| 檔案 | 表 0 筆數 | NEW | HOT | 推薦 | 古典 | 隱藏 |
|------|-----------|-----|-----|------|------|------|
| 官方 `ServerConfigND.dat`（本機那份） | 1067 | 11 | 0 | 0 | 0 | 29 |
| **[NX]Patch 3.0 `patch Datas\config2`** | **1148** | **40** | **26** | **36** | **54** | **23** |

官方那份 11 首 NEW：`103 木马屠城记`、`141 Thank You`、`460 ariari`、`1222 black dragon`、
`1294 night of fire`、`1299 卡农`、`1300 1-1234 Back`、`1353 Blaze`、`1412 Asereje`、
`5017 Phantom of Blue`、`5018 Jihad`。

NX 那份表的前段是 id 升冪（1, 6, 26, 39, 40, 43…），**尾段是作者後來追加的歌**（不再升冪）——
因為畫面是反序顯示，這些追加的歌就出現在選單最上面。

---

## 8. 重製版接在哪

（在 `feat/song-loader` 分支上；外部歌包才有「自己的 serverconfig」這回事。）

| 項目 | 位置 |
|------|------|
| 解混淆 + 解析 12-byte 表 | `Sdo.Osu/SdoServerConfig.cs`（純邏輯，`Assets/Tests/EditMode/SdoServerConfigTests.cs`） |
| 外部 `.gn` 歌包掃描時讀包內的 serverconfig | `Sdo.Osu/ExternalSongScanner.ApplyServerConfig` → `ExternalSong.Badge` / `.PackOrder` / `.PackHidden` |
| 帶進歌單 | `Game/SongCatalog.Entry.badge` / `.packOrder`（`Game/ExternalSongLibrary.ToEntry` 填） |
| 排序（包用自己的順序） | `UI/Catalog/SongListModel.BrowseKey` + `Curate`；**分類抽屜的「資料夾」模式**在 `UI/Catalog/SongGrouping.ByPackOrderThenTitle`（那條才是選歌畫面實際在用的清單） |
| 分頁 | 最新 = NEW 標籤、懷舊 = 古典(CLASSICAL) 標籤（勁樂 這邊改當「資料夾/分類瀏覽」用，不接 HOT） |
| 標籤決策（包優先，官方歌退回「最上面 N 首 = NEW」） | `UI/Catalog/SongListModel.BadgeMap` |
| 四種標籤繪製（優先序同官方） | `UI/Screens/SongSelectScreen.SetRowBadge` |

找檔順序（相對 `.gn` 所在資料夾，通常是 `<pack>\patch music`）：
`.\` → `..\patch Datas\` → `..\Datas\` → `..\`，每層都試 `ServerConfigND.dat` / `config2` / `serverconfig.dat`。
serverconfig 住在**隔壁**資料夾、不在掃描快取的簽章裡，所以快取命中時也會重讀一次。

刻意**偏離官方**的一點：`+7`（未開放）只記錄成 `PackHidden`，**不把歌藏起來** —— 玩家自己放進來的歌不該被包的旗標弄不見。

美術：`HOT.AN` / `RECOMMEND.AN` / `CLASSICAL.AN` 只在線上樹（`assets\閉撰敃氪\DatasSDO\UI\ROOMDLG`），
離線 Extracted 沒有。打包版由 `tools/package_build.ps1` 的 `online ROOMDLG` overlay 自動帶進 `DATA\UI\ROOMDLG`；
**編輯器**用的是 `data_root.txt` 指的那棵 DATA，需要手動把這三個檔補進去（少了就只有 NEW 標籤會顯示）。

工具：`tools/nx/decode_nx_config.py`（解 config1/config2 → 明碼，並印出歌曲表與有標籤的歌）。
