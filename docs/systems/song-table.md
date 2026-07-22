# 歌曲表 song_table.csv（全部歌曲資料的唯一來源）

`65/My project/Assets/StreamingAssets/song_table.csv` —— **一列 = 一個 `.gn` 檔**（4325 列）。
遊戲執行期只讀這一個檔；歌單、難度、音符數、歌名、BPM、單首 offset、解密 seed 全在裡面。

以前這些東西分散在四份 JSON：

| 舊檔 | 內容 | 現在在哪 |
|------|------|----------|
| `gn_header_catalog.json` | `.gn` 表頭：難度/音符數/長度/小節、簡繁英歌名、曲師 | `lv*` `notes*` `dur*` `meas*` `title*` `artist*` `producer` `origName` 欄 |
| `gn_keytable.json` | 解密：`enc`/`seed`/`innerOff`/`size` | 同名欄位 |
| `song_catalog.json` | 遊戲歌單（完全是表頭的衍生物） | 已無獨立來源，就是上面那些欄 |
| `song_name_overrides.json` + `.csv` | 手改的歌名/BPM/offset（key = 詞幹） | `title` `artist` `bpm` `offsetMs` `src` 欄，寫在該詞幹的每一列 |

四份都以 `.gn` 檔名為 key、內容大量重疊、又各有各的重建工具 —— 只要有一份沒重跑就對不起來
（歌單有這首、金鑰表沒有 → 選得到但解不開）。整併成一張表之後，這種 skew 從結構上就不存在了。

檔案是 **UTF-8-BOM**（Excel 繁中直接開不亂碼）、LF、依 `gn` 升冪排序。

---

## 一首歌 = 兩列

原始資料每首歌有兩份譜：`sdomNNNN**k**.gn`（鍵盤）與 `sdomNNNN**t**.gn`（毯子），
**fileId 不同、難度不同**（`sdom0001`：k = LV 3/4/5、easy 510 notes；t = LV 1/3/5、easy 284 notes），
所以它們是兩列。共用的只有「顯示」那幾欄：

- `title` / `artist` / `bpm` / `offsetMs` / `src` —— 兩列必須一致，寫檔時自動由 **k 列**同步過去
  （`tools/song_table.py` 的 `sync_display`）。要改歌名，改 k 那一列就好。
- `producer` **不同步**：k 譜與 t 譜常常是不同人打的（`sdom0002k`=Tina / `sdom0002t`=S.Q.H）。

重製版是純鍵盤，所以**給人瀏覽的清單只該出現 k 列**（`SongCatalog.Primary`，2166 首）；
以 gn 反查歌名、字型預熱才用 `SongCatalog.All`（4325 列）。

## 欄位誰說了算

| 欄位 | 誰寫的 | 手改會不會被蓋掉 |
|------|--------|------------------|
| `title` `artist` | 人（或 `build_song_name_overrides.py` 打底） | 不會，merge-preserve；只有明確加 `--songname` 才會被原版歌名表蓋掉 |
| `bpm` `offsetMs` | **只有人**（`offsetMs` 靠耳朵調，沒有來源可重建） | 重掃譜面的工具一律不碰；`bpm` 只在空的時候被填 |
| `fileId` | `.gn` 表頭，但可人工指定（撞號插隊：`sdom1117_1` 借 11138 槽） | `refresh_gn_header_stats.py` **刻意不碰** |
| `lv*` `notes*` `dur*` `meas*` `chartBpm` `producer` `origName` | 譜面本身 | 會，重掃就以譜面為準 |
| `enc` `seed` `seed1` `seed2` `innerOff` `size` | `gn_keytable.py` 暴力還原 | 會 |

`bpm` 是**顯示** BPM（選歌資訊面板、房間標籤），`chartBpm` 是 `.gn` 表頭的原值。
兩者都不影響遊戲時間軸 —— 判定與流速一律讀譜面本身。

## 工具

```
tools/song_table.py                  讀寫模組（load / by_gn / save / stem / is_primary）
tools/gn_keytable.py <music>         暴力還原每個 .gn 的 seed → enc/seed/innerOff/size 欄
tools/build_gn_header_catalog.py     解 .gn 表頭 → 難度/音符數/歌名欄（已有的 title/artist 不蓋）
tools/build_song_name_overrides.py   歌名打底(songlist.dat / k.gn) → title/artist 欄；--songname 才讀原版歌名表
tools/refresh_gn_header_stats.py     只刷譜面數值，歌名/BPM/offset/fileId 全保留
tools/add_songs_incremental.py       只 upsert 指定的 .gn（加幾首歌別全量重掃）
tools/remove_songs.py                刪資源檔 + 從表移除
tools/measure_song_offsets.py        量音檔與譜面的錯位 → --apply 寫 offsetMs
tools/song_manager.py                Tkinter GUI（上面這些的殼）
tools/build_song_table.py            一次性搬家：四份舊 JSON → song_table.csv
```

**加少量歌請走 `add_songs_incremental.py`，不要全量重掃** —— 重掃會把 music 目錄裡的實驗檔/殘骸
（`sdom2705k_edit.gn`…）一起收進來，還會產生上萬行 git diff。

**`SONGNAME.TXT` 預設不讀。** 那是原版遊戲自己的顯示歌名表（`H:\sdo\熱舞 Online(金富貴寶寶)\DATA\`，
Big5、575 首），不在本 repo、遊戲執行期也不讀它 —— 它的用途只有一次性把官方歌名灌進表，而那件事
早就做完了。它是**無條件覆蓋**的，開著就會每次重跑把手改的 575 首打回官方版（周杰倫→周傑倫、
七里香→七裏香的成因）。要重灌才加 `--songname`。它自己有錯字的那幾首釘在
`build_song_name_overrides.py:KNOWN_CORRECTIONS`（最高優先，連 `--songname` 也蓋不掉）。

## Runtime

| 類別 | 角色 |
|------|------|
| `SongTable` | 唯一的載入者（CSV parser；欄位**照表頭名字**取，加欄/調順序都不會解錯格） |
| `SongCatalog` | 歌單視圖：`All` / `Primary` / `Get` / `Matches` / `MainOggName` |
| `GnKeyTable` | 解密視圖：`SeedsFor(gn)` 餵給 `GnChart.Load` |
| `GnHeaderCatalog` | 原文歌名視圖：`{zhCN, zhTW, en}` |
| `SongTableWriter` | 譜面編輯器 Ctrl+S 寫回 `offsetMs`（只換那一格，其餘位元組不動） |

CSV parser 是自己寫的（Unity 沒有內建）：支援引號欄、欄內逗號/換行、`""` 跳脫、CRLF、BOM。
歌名裡真的有逗號（`"Sexy, Free & Single"`），照 `Split(',')` 切會讓後面每一欄一路錯位。

文字全部是 UTF-8：原始資料是 GB2312(cp936)，而這個 runtime（.NET Standard 2.1 / IL2CPP）沒有
cp936 codec，裝置上解只會得到 mojibake 而且隨 OS locale 飄 —— 所以解碼一律在 import 時由 `tools/` 做掉。

測試：`Assets/Tests/EditMode/SongTableTests.cs`、`SongTableWriterTests.cs`。
