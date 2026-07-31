# NX 工具箱 — NXPatch 私服客戶端的譜面格式與破解

`.nx` 是 **NXPatch.exe**（Super Dance Online 私服/線上客戶端）的譜面容器。
它跟舊的 `k.gn` 很像但**不一樣**，而且多了兩個功能：**捲動速度變化**與**炸彈**。
這個資料夾放逆向的成果：格式規格、啟動繞過、以及取得解密譜面的工具。

## 先講結論

> **`.nx` 已完全破解，可以離線解，不用進遊戲、不用連伺服器。**
>
> ```
> python crack_nx.py "H:/sdo/Super Dance Online/patch music/*.nx" -o out/
> ```
>
> 加密是兩層：patcher 的**無金鑰固定變換**（`^0xA7 → -0x29 → ×0xF9 → ror3`）+ 遊戲原本的
> **0x3D09 LCG**。而 LCG 的 seed 也不必連線拿 —— blob 解出來的前 300 bytes 是「重複表頭」，
> 內容就是檔案 `0x1C8` 那份**明文**表頭，拿它當已知明文即可反推（seed 還會跨檔重複用）。
> 細節見 [`NX_FORMAT.md`](NX_FORMAT.md) §2。
>
> 已驗證：`sdom2818K.nx` 離線解出的 1,384,076 bytes 與從遊戲記憶體 dump 的明文 **byte-for-byte 完全一致**。

> ⚠️ **更正紀錄**：本文件較早的版本寫「離線解不開、只能連伺服器 dump」。那是只拿 `patch music\*.nx`
> 試「重複表頭已知明文」失敗就下的結論 —— 當時**漏看了 patcher 對解密函式的 inline hook**（外層那道
> 固定變換），所以已知明文對不上。補上外層還原後即可離線破解。`dump_chart.py`（執行期撈明文）仍然
> 保留，但現在只是**交叉驗證**用，不再是唯一手段。

## 檔案

| 檔案 | 用途 |
|------|------|
| [`NX_FORMAT.md`](NX_FORMAT.md) | **格式規格**：容器版面、StepFile 表頭、加密機制與金鑰來源、note 格式（frame/note type）、**捲動速度 frame_type 33**、**炸彈 note_type 1** |
| [`NXSTART.md`](NXSTART.md) | **啟動握手怎麼繞過**：LaunchStamp + seal + 共享記憶體，含校驗算法與 Python 版 |
| [`NXStart.cs`](NXStart.cs) | 最小替代啟動器原始碼（C#，來源見 NXSTART.md） |
| [`crack_nx.py`](crack_nx.py) | **主力工具**：離線破解 `.nx` → 明文譜（外層還原 + LCG seed 反推）。**實測 199/199 全解** |
| [`nx_to_gn.py`](nx_to_gn.py) | **整包轉檔**：`.nx` → 標準 `k.gn` ＋金鑰表＋歌單 sidecar，重製版可以直接把整包當外部歌曲庫載入（見下） |
| [`dump_chart.py`](dump_chart.py) | 執行期從遊戲記憶體 hook 解密函式撈明文（現在只當**交叉驗證**用，非必要） |
| [`decode_chart.py`](decode_chart.py) | 解析 dump 出來的明文譜：frame/note 統計、**捲動速度表**、**炸彈表** |
| [`dump_nxpatch_image.py`](dump_nxpatch_image.py) | spawn NXPatch 並 dump 記憶體映像（靜態逐位址分析用；平常不需要） |
| [`install_bomb_assets.py`](install_bomb_assets.py) | 把炸彈用的 StepMania 素材裝進打包來源樹（見下） |

需求：`pip install frida`（`dump_*.py`）。`decode_chart.py` 只用標準庫。

## 炸彈素材（clone 之後要跑一次）

炸彈的**引爆特效圖**和**爆炸音**來自外部 StepMania，而 `assets/` 整棵被 `.gitignore`
（本專案所有遊戲資料都不進版控），所以 **clone 之後這兩個檔不會在**，build 出來的炸彈會沒圖沒音。

```
python tools/nx/install_bomb_assets.py
```

它會裝進**打包來源樹**，之後 `tools/package_build.ps1` 就會自動鏡射進 build 的 `DATA/`
（**不需要改打包腳本**，因為 `Extracted/` 和 `SE/` 本來就整棵複製）：

| 來源（StepMania） | 裝到 | 用途 |
|---|---|---|
| `NoteSkins/common/default/Fallback Tap Explosion Dim HitMine.png` | `assets/sdox_offline/Extracted/NOTEIMAGE/BOMB_EXPLODE.png` | 引爆特效圖 |
| `Themes/CyberiaStyle 6 .../Sounds/Player mine.ogg` | `assets/sdox_offline/SE/player_mine.wav` | 引爆音（轉成 wav，`PlaySe` 只讀 `SE/*.wav`；需 ffmpeg） |

> 炸彈**本體**的圖不用裝 —— 那是遊戲自帶的 `NOTEIMAGE/NOTEIMAGE_*/ZD00..ZD03.PNG`，會跟著 note skin 換。
> 編輯器吃的是 `data_root.txt` 指到的那棵 DATA，需要的話把這兩個檔一併複製過去。

## 取得解密譜面的流程（離線，兩步）

```
python crack_nx.py "H:/sdo/Super Dance Online/patch music/*.nx" -o out/
python decode_chart.py out/sdom2818K.nx.plain --diff hard
```

實測全量結果：**199/199 成功，distinct seed = 43**（seed 跨檔重複用，多數檔直接命中 pool，
只有新 seed 才需要暴力 2²⁴，整批數分鐘內跑完）。

`music\*.gn`（已安裝的、沒有外層變換）同樣可解，`crack_nx.py` 會自動處理。

<details><summary>（選用）用執行期 dump 交叉驗證</summary>

```
1. 照平常方式啟動遊戲並登入 → 2. python dump_chart.py → 3. 進遊戲玩那首歌
4. 產出 dumped_seed<seed>_len<n>.bin，可與 crack_nx.py 的輸出逐位元組比對
```
（`sdom2818K.nx` 已這樣驗過：兩者 1,384,076 bytes 完全一致。）
</details>

`decode_chart.py` 對 sdom2818（"3" by Laur）的實測輸出：

```
file_id=12818  BPM=210.0  levels=[16, 23, 36]
note_count=[1124, 1286, 1611]（含炸彈）
note_type : {'一般音符': 1525, '炸彈': 20, '長條頭': 33, '長條尾': 33}   ← 1525+33+33+20 = 1611 ✓
捲動速度 frame_type 33（80 個；base 1000 = ×1.0）
   小節  1: b1.0=2000(×2)
   小節  7: b1.5=250(×0.25)[線性5.5拍]
   ...
炸彈 note_type 1（20 顆）
```

## 主要發現摘要

- **容器**：`0x00` 起 6 個資源名 → `0x1C8` StepFile 表頭（**明文**）→ `0x2F4` 起加密 blob。
  `music\sdomNNNNK.gn` 也是同一個格式（副檔名叫 .gn，內容不是舊 ddrm/sdom）。
- **加密**：0x3D09 LCG；blob 在 `0x2F4`、長度 `filesize−0x2F4`；seed 來自執行期 `[ctx+0x90]` 的
  ddrm 標頭（**server-provided**）。`NXPatch.exe` 相關位址：解密 `0xb1c950`、容器讀取 `0xd6e9b0`、
  舊 ddrm 讀取 `0xd6c360`、LoadNote `0xd67220`。
- **捲動速度** = `frame_type 33`：低16位 = 速度值（**1000 = ×1.0**），高16位 = **線性變速時長**
  （1/48 拍，0 = 瞬間）。已用官方編輯器的位置交叉驗證吻合。
- **炸彈** = 音軌幀的 `note_type 1`（要避開，不是拿來打的），`note_count` 有把它算進去。
- **NXPatch.exe 沒有真的加殼**，主 `.text` 是明文可讀，所以載入/解密流程能純靜態分析出來。

## 重製版接在哪

`Sdo.Osu/GnChart.cs`（解析）、`Sdo.Osu/OsuBeatmap.cs`（`ScrollSpeeds`/`CurrentScrollSpeed`）、
`Game/ScreenGameplay.cs`（`ScrollPx` 套捲動、`TickBombs`/`ExplodeBomb` 炸彈）。
對照表見 [`NX_FORMAT.md`](NX_FORMAT.md) §4。

---

## 整包當外部歌曲庫玩（`.nx` → `k.gn` → 選歌畫面）

`crack_nx.py` 解出來的是**明文譜**，適合分析；要真的在重製版裡玩，用 [`nx_to_gn.py`](nx_to_gn.py)
把整包轉成**標準 SDOM `.gn`**，遊戲的外部歌曲掃描就能直接吃這個資料夾。

```bash
python tools/nx/nx_to_gn.py "<pack>"
# patch 沒帶的封面/編舞，再指到遊戲本體補（包自帶的優先，這些只當補件）：
python tools/nx/nx_to_gn.py "<pack>" --icons "H:/65_remake_clean/DATA/UI/MUSIC/ICONS" --dance "H:/65_remake_clean/DATA/DANCE"
```

補件前後（[NX] 3.0，199 首）：封面 140 → **198**、編舞 35 → **190**、試聽 197。

轉完把 pack 根目錄加進 `config.ini` 的 `AdditionalSongFolders`，開機掃描就會多出一個以 pack
資料夾命名的分頁。實測 **199/199 首全部轉換成功、597 張譜（199×3 難度）全部解得開**。

### 產物（都寫在 `patch music/` 裡，原始 `.nx` 保留不動）

| 檔案 | 內容 |
|------|------|
| `sdomNNNNK.gn` | 標準 SDOM `.gn`：456B 資源名前綴 + 300B 明文表頭 + LCG 密文 |
| `gn_keytable.json` | **金鑰表**，schema 同 `tools/gn_keytable.py` 的 `gn-keytable/1`。**下次再跑直接讀這張表，不用重算** |
| `sdo_pack.tsv` | 歌單 sidecar（UTF-8）：seed、表頭數值、歌名/歌手、音樂/封面/試聽/編舞的路徑 |
| `cd/<fileId>.png` | 封面轉出來的 PNG（見下） |

sidecar 的路徑：包內資源寫**相對**（整包搬家還能用），補進來的包外資源寫**絕對**
（相對會變成一長串 `../../../..`，Songs 一搬就全斷）。

### 封面一定要轉成 PNG

官方 ICONS 有 92% 是 `.dds`（這包 146 張裡 134 張，全是未壓縮 A8R8G8B8 237×237）。遊戲端的共用圖片
載入器只吃 PNG/JPG/BMP，而繞去 `DdsLoader` 有兩個坑：它每個解碼器最後都
`Apply(..., makeNoLongerReadable: true)`（拿不回像素、翻不了列），且 DDS 是「第一列在上」而 Unity 貼圖
第一列在下 —— 3D 的 UV 會自動抵銷，UI sprite 不會，畫出來會上下顛倒。所以一律離線轉一份 PNG
（順便 224 KB → ~75 KB）。`ExternalCdImage` 仍留了一條未壓縮 DDS 的後備解碼路徑，給沒跑過本工具的包用。

### 為什麼非要有金鑰表

離線單機版整個語料（4000+ 檔）只用 ~150 把共用 seed，所以 runtime 有一個共用池，硬試也試得出來。
**[NX] 這包不是**：實測 199 首**每首自己一把**（去重後 43 把，且與共用池零交集）——
共用池一把都開不了。所以 seed 必須先算好存表帶著走，`sdo_pack.tsv` 的 `seed` 欄就是這個用途。

### 兩份表頭不一致的坑

容器裡有**兩份** 300 bytes 表頭：`0x1C8` 那份明文，和密文區解開後的「重複表頭」。這包裡兩份**不一樣**——
明文那份被 patch 改成英文歌名，而且 `address_end`(+296) 是垃圾值；密文那份才是引擎真正解析的。
重製版的 `GnChart` 又要求兩份**逐位元組相同**才算解密成功，所以轉檔時把解密後那份（權威、位址正確）
寫回明文槽。英文歌名沒有掉——它從 `SongList.dat` 讀出來寫進 `sdo_pack.tsv`（那本來就是遊戲畫面顯示的來源）。

### 遊戲端接在哪

`Sdo.Osu/SdoPackIndex.cs`（讀 sidecar）、`Sdo.Osu/GnHeader.cs`（不解密就讀表頭數值）、
`Sdo.Osu/ExternalSongScanner.cs`（`SongFormat.Gn` 掃描）、`Game/ExternalCdImage.cs`（`.dds` 封面）、
`Game/ScreenGameplay.cs`（`chartFormat == 3` 用自己的金鑰載譜）。
