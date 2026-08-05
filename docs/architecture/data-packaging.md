# DATA 封裝(SDOPAK)

原版 SDO 的資料樹是 **103,275 個檔、14 GB**,其中 `AVATAR/` 一個目錄就佔 67,503 個小檔。
這份文件定義把它封裝成少數幾個 `.pak` 的格式、掛載規則、以及哪些東西**不**進 pak。

目標有三個,按重要性排:

1. **載入速度** —— 大量小檔的內容讀取快 **4–8 倍**(AVATAR 有 67,503 個小檔);
   代價是開機多 300ms 的索引載入。實測見 §2.2。
2. **發佈與更新** —— 分卷 + patch 疊加,改一個貼圖不用重發 14 GB;順帶省下 2.9 GB(§2.0)。
3. **混淆** —— 讓資料不能被直接拷走使用、不能被隨手改檔作弊。

> ⚠️ **第 3 點只是混淆,不是保護。** 用戶端的金鑰必然在執行檔裡,有決心的人幾十分鐘就能取出
> ——我們自己就是這樣把原版 SDO 拆開的。不要對它有超出「防君子」的期待,也不要為了它犧牲
> 前兩點或讓自己的工具鏈變難用。

---

## 1. 磁碟版型

```
Build/Windows/
  dance.exe   dance_Data/   MonoBleedingEdge/
  log.txt   log.txt.prev   used_files.txt
  screensave/
  DATA/
    base_core.pak            ← 唯讀 pak
    base_avatar.pak
    base_motion.pak
    base_scene.pak
    base_se.pak
    music_000.pak … music_NNN.pak
    patch_001.pak …          ← 之後的更新只丟新檔進來
    packed_dirs.json         ← 打包器產出：哪些目錄進了 pak（package_build 靠它決定刪誰）
    BGM/                     ← 散裝：玩家最可能想自己換掉的東西（見 §2.1）
    PROFILE/                 ← 明碼、可寫
    ADDON/                   ← 明碼、可寫
    CACHE/                   ← 明碼、可寫、可整個刪
    REPLAY/                  ← 明碼、可寫
```

**分界線:`DATA/` 底下只有 pak 是唯讀的,四個 reserved 目錄是可寫的。**
`BGM/` 是刻意留的散裝資料夾(§2.1),不是 reserved —— 散裝層的優先權在所有 pak 之上,
所以「不打包」在 VFS 那邊零成本,不需要任何特別處理。

- `DATA/` 的 pak 可以整組刪掉重下載,不會碰到任何玩家資料
- `CACHE/` 可以整個刪掉,遊戲自己重建
- `PROFILE/` 是唯一「刪掉會痛」的,備份就備這一個
- `ADDON/` 可以整包搬到別台機器;`config.ini` 的 `AddonFolder=` 也能指到別的碟

`PROFILE` / `ADDON` / `REPLAY` 維持現在的位置,所以
[`SdoDataRoot.ProfileDir`](../../65/My%20project/Assets/Scripts/Sdo.Settings/SdoDataRoot.cs)、
[`SdoExtracted.AddonDir`](../../65/My%20project/Assets/Scripts/Game/SdoExtracted.cs)、`ReplayDir`
都不用改,也不需要寫存檔遷移程式。

### 1.1 不得有東西落在 `Build/Windows` 之外

這是硬性要求。目前有四處會漏,全部要修:

| 漏到哪 | 誰寫的 | 修法 |
|---|---|---|
| `%USERPROFILE%\AppData\LocalLow\<company>\<product>\Player.log` | Unity 引擎(`usePlayerLog: 1`) | 設成 **0**。[`SdoLog`](../../65/My%20project/Assets/Scripts/Game/SdoLog.cs) 已掛 `Application.logMessageReceivedThreaded`,引擎訊息本來就進 exe 旁的 `log.txt`,關掉幾乎零代價 |
| 同上目錄(`Application.persistentDataPath`) | `ExternalSongLibrary` 掃歌快取、`SdoLog` fallback、`EftEffect` debug 軌跡 | 改指 `DATA/CACHE/` |
| `%LOCALAPPDATA%\Temp\<company>\<product>\`(`Application.temporaryCachePath`) | `OsuKeysoundBank` 解出的 keysound | 改 `DATA/CACHE/KEYSOUND/`,開機清空 |
| `HKCU\Software\<company>\<product>`(**登錄檔**) | `PlayerPrefs` —— `AvatarTuner`(build 版也照吃)、`ChartEditorScreen` | 包一層 `LocalPrefs` 寫 `DATA/PROFILE/prefs.ini` |

#### 實測結果(2026-08-05,`Dance v1.9.0-dev-8f1a3`)

把三個外部位置清空 → 跑一輪打包版 → 再看有沒有東西長回來:

| 位置 | 結果 |
|---|---|
| `%USERPROFILE%\AppData\LocalLow\DefaultCompany\<product>\` | **完全沒產生** ✅ |
| `%LOCALAPPDATA%\Temp\DefaultCompany\<product>\` | **完全沒產生** ✅ |
| `HKCU\Software\DefaultCompany\<product>` | 仍會產生,但 **22 筆全是 Unity 引擎自己的**,我們的一筆都沒有 |

那 22 筆的組成:

- `Screenmanager *` ×14 + `UnitySelectMonitor` —— 視窗大小 / 位置 / 全螢幕 / 顯示器選擇。
  由引擎的 ScreenManager 直接寫,**沒有任何 Player Setting 可以關**。
- `unity.player_session_count` / `unity.player_sessionid` / `unity.cloud_userid` +
  `unity_connect.*` ×3 —— 遙測。把 `com.unity.modules.unityanalytics` 從
  `Packages/manifest.json` 拿掉可以消掉這 6 筆,但消不掉上面那 15 筆,**登錄檔那個 key 還是會存在**。

**結論:登錄檔那一項做不到歸零,只能做到「裡面沒有我們的東西」——而那已經達成。**
要真正歸零只剩包 launcher 這條路(用 `-crashReportPath` 之類的啟動參數),成本遠高於效益。

**另一個已知殘留**:Unity standalone 的 crash handler 會往
`%LOCALAPPDATA%\Temp\<company>\<product>\Crashes\` 寫 dump,同樣只能靠 launcher 參數。
只有真的 crash 才會產生,目前接受這個例外。

**順帶注意**:build 會把 `productName` 戳成 `dance <版本>`,所以**每個版本號都會各自開一個
LocalLow 資料夾與登錄檔 key**。這本身就是該把東西搬進 build 資料夾的另一個理由。

---

## 2. 分卷

按「更新頻率」而不是「目錄結構」切。改一個 UI 貼圖不該讓玩家重載 4 GB 的 AVATAR。

| 卷 | 內容 | 大小 | 壓縮 | 加密 |
|---|---|---:|---|---|
| `base_core.pak` | `UI` `EFFECT` `3DEFT` `3DNOTES` `NOTEIMAGE` `ITEM2D` `DAOJU` `EMBLEM` `LOADING` | ~380 MB | deflate | 全檔 |
| `base_avatar.pak` | `AVATAR` | ~4.0 GB | deflate | 全檔 |
| `base_motion.pak` | `MOTION` `AUMOTION` `DANCE` `CAMERA` | ~820 MB | deflate | 全檔 |
| `base_scene.pak` | `SCENE` | ~82 MB | deflate | 全檔 |
| `base_se.pak` | `SE` | ~52 MB | store | 表頭 |
| `music_NNN.pak` | `MUSIC`,每卷約 1 GB | ~8.3 GB | store | 表頭 |
| **(不打包)** | `BGM` | ~12 MB | — | — |
| `patch_NNN.pak` | 任意 | — | 同來源 | 同來源 |

### 2.0 實測(2026-08-05,`H:\65_remake_clean\DATA`)

| 卷 | 檔數 | 原始 | 打包後 | 比例 | 耗時 |
|---|---:|---:|---:|---:|---:|
| `base_avatar` | 67,503 | 4,010.6 MB | 1,731.0 MB | **43%** | 1424s |
| `base_core` | 14,769 | 404.5 MB | 315.2 MB | 78% | 151s |
| `base_motion` | 11,600 | 818.3 MB | 578.4 MB | 71% | 297s |
| `base_scene` | 2,594 | 82.4 MB | 34.3 MB | 42% | 33s |
| `base_se` | 90 | 51.5 MB | 51.5 MB | store | 0.2s |
| `music_000..008` | 6,536 | 8,316 MB | 8,316 MB | store | 72s |
| `BGM`(散裝) | 8 | 12.1 MB | — | — | — |

**103,100 個檔 → 15 個 pak + 一個 BGM 資料夾;14 GB → 約 11 GB。** 全打一次約 33 分鐘。

> AVATAR 的 DDS 壓到 **43%** 遠超原本的預估(以為 DXT 已經壓過、deflate 只剩 10–15%)。
> 原因是官方那批 DDS 有大量重複區塊(共用模板、大面積純色),deflate 吃得很好。
> 這也表示**壓縮不是白做的** —— 省下的 2.3 GB 幾乎全來自 AVATAR 這一卷。

### 2.1 音訊:打包,而且**不落地**

`SE` 與 `MUSIC` 有打包(store + 只加密表頭 4 KB);**`BGM` 刻意維持散裝**
—— 那是玩家最可能想自己換掉的東西,散裝資料夾丟進去就生效。

**問題**:Unity 沒有記憶體 ogg 解碼器 —— `UnityWebRequestMultimedia` 只吃 `file://`,
而 `Mp3Decoder.Decode` 吃的是路徑。pak 內的音訊對它們來說等於不存在,
症狀是**靜默無聲而且不報錯**。

**解法**:自己帶解碼器,全部從記憶體解。入口是
[`MemoryAudio`](../../65/My%20project/Assets/Scripts/Game/MemoryAudio.cs)
——「VFS 位元組 → 看**內容**判格式 → PCM → `AudioClip.Create`」。

| 格式 | 解碼器 | 授權 |
|---|---|---|
| ogg | `sdovorbis.dll`([`tools/sdovorbis`](../../tools/sdovorbis),stb_vorbis 包裝) | public domain |
| wav | [`WavDecoder.cs`](../../65/My%20project/Assets/Scripts/Game/WavDecoder.cs)(自己 parse RIFF) | — |
| mp3 | `sdomad.dll`([`tools/sdomad`](../../tools/sdomad),libmad,與 StepMania 逐位相同) | **GPL v2** |

> ⚠️ **`libmad` 是 GPL v2** —— 只要出貨包含 `sdomad.dll`,整個散布的作品就要以 GPL v2 授權
> 並附原始碼。那是加 libmad 當下就決定的事,跟打包無關。stb_vorbis 是 public domain,
> 不會再增加義務。

#### 為什麼 ogg 可以換解碼器、mp3 不行

mp3 沒有精確的樣本位置:編碼器延遲要靠 Xing/LAME 表頭猜,壞幀處理各家不同 ——
`sdomad` 才要逐條照抄 StepMania 的錯誤處理(見那邊的 README:NLayer 會讓某些歌晚 **0.62 秒**)。

**Vorbis 沒有這個問題。** 格式本身帶 granule position,天生 gapless,任何合規解碼器輸出的 PCM 都一致。
StepMania 自己的 `RageSoundReader_Vorbisfile.cpp` 也沒有任何偏移補償(只用 `ov_pcm_tell` 追位置)
—— 那就是證據。

**而且 mp3 永遠不會從 pak 讀**:官方 `MUSIC` 是 **100% ogg**(4,356 ogg + 2,180 `.gn`),
mp3 只出現在 `ADDON/SONG`(外部歌,reserved 目錄、永不打包)。所以那一整套 gapless/priming
修正**一行都沒動**。

#### 呼叫點的規則

**有實體就走原路,沒實體才從記憶體解。** 六處(遊玩主音訊、環境音、UI 音效、大廳 BGM、
選歌試聽)統一成這條。散裝時完全走原本的路徑,行為零變化。

實測(打包版、加密 pak):

```
[Step1] pak 內音訊走記憶體解碼:sdom5085.ogg (94.810s, 2ch, 44100Hz)
CACHE/AUDIO: 不存在        ← 一個檔都沒落地
錯誤: 0
```

`94.810s` 與離線自測(`tools/sdovorbis/selftest.exe`)、EditMode 測試三邊一致。

### 2.2 載入速度:內容讀取快 1.46×,開機慢 300ms

用 `SDO_PROBE=1` 觸發全資產讀取(同一份 build,只換 `data_root.txt`)。
兩邊 `touched=151038 / missing=43010` 完全一致 —— 做的是同樣的工作。

| 區段 | 檔數 | pak | 散裝 | 倍率 |
|---|---:|---:|---:|---:|
| keep-whole trees(AVATAR 為主) | 68,196 | **62.9s** | 257.4s | **4.1×** |
| UI art | 6,766 | **5.1s** | 38.9s | **7.6×** |
| note/effect skins | 935 | 0.5s | 4.5s | 9.0× |
| scenes+mapobjs | 2,765 | 5.2s | 16.9s | 3.3× |
| cameras | 1,212 | 0.5s | 3.9s | 7.8× |
| songs | 71,051 | 211.5s | **94.8s** | **0.45×** |
| **總計** | 151,038 | **285.9s** | **416.9s** | **1.46×** |

**大量小檔那幾段 pak 快 4–8 倍** —— 那正是原本要解決的痛點(AVATAR 有 67,503 個小檔)。

#### 但開機階段 pak 慢約 300ms

量「log 首行 → `[shop] catalog:`」:散裝 **2.185s** / **2.143s**(先讀完 10.8 GB pak 擠快取,
結果沒有差別)、pak **2.480s**。

> **開機那條路是 metadata 工作負載,不是內容讀取。** 商城目錄建構做的是
> `Directory.GetFiles` + `File.Exists`,打的是 NTFS 的 MFT 而不是檔案資料 —— 10 萬筆 MFT 記錄
> 只有幾十 MB,永遠是熱的,散裝版根本沒有「冷開檔」可言。反過來 pak 要先把 10 萬筆索引
> 解密、解壓、建 Dictionary,那 300ms 是純粹多出來的固定成本。

**結論:開機付 300ms,換來逛商城 / 換裝 / 載場景時 4–8 倍的內容讀取速度。**

#### 已知效能缺陷:`PakProvider` 只能整份讀

songs 那段 pak 慢 2.2 倍,原因是
[`PakProvider.ReadAllBytes`](../../65/My%20project/Assets/Scripts/Sdo.Settings/Vfs/PakProvider.cs)
**把整個條目讀出來並驗 CRC**,而 probe 只要前 64 bytes —— 一首 3–8 MB 的 mp3 為了看 64 bytes
被整份讀出來。

實務影響有限(真的播歌本來就要讀整份),但 `store` 的條目其實可以用**有界串流**直接讀:
沒有壓縮就不需要整份解開,加密也只是 CTR 的偏移量計算。`OpenRead` 對
`compression == store` 走這條路就能消掉這個缺陷。目前沒做 —— 已知待辦。

---

## 3. 檔案格式(SDOPAK v1)

小端序。所有 offset 都是自檔頭起算的絕對位移,除非另有說明。

### 3.1 Header(64 bytes)

| 位移 | 型別 | 欄位 | 說明 |
|---|---|---|---|
| `0x00` | `u8[8]` | `magic` | `"SDOPAK\x00"` + 版本 byte `\x01` |
| `0x08` | `u32` | `formatVersion` | 1 |
| `0x0C` | `u32` | `flags` | bit0 = 索引已加密;bit1 = 資料區有加密條目 |
| `0x10` | `u32` | `entryCount` | |
| `0x14` | `u32` | `pakId` | 金鑰派生用;同時是同前綴卷的排序鍵 |
| `0x18` | `u64` | `indexOffset` | |
| `0x20` | `u32` | `indexStored` | 索引區在檔案裡的大小(壓縮+加密後) |
| `0x24` | `u32` | `indexRaw` | 索引區解開後的大小 |
| `0x28` | `u64` | `dataOffset` | 資料區起點 |
| `0x30` | `u8[16]` | `indexMac` | HMAC-SHA256(索引密文) 取前 16 bytes |

### 3.2 索引區

整段先 deflate、再 AES-CTR 加密。開機時一次讀進來,解開後建 `Dictionary<ulong, Entry>`。
100k 筆約 4 MB,載入是毫秒級。

```
u32          pathBlobSize
u8[]         pathBlob        全部路徑，UTF-8，'\0' 分隔（正規化形式，見 §4.1）
Entry[entryCount]            依 pathHash 升冪排序
```

`Entry`(40 bytes,固定大小):

| 位移 | 型別 | 欄位 | 說明 |
|---|---|---|---|
| `0x00` | `u64` | `pathHash` | FNV-1a 64(正規化路徑,見 §4.1) |
| `0x08` | `u32` | `pathOffset` | 在 `pathBlob` 中的位移 |
| `0x0C` | `u32` | `rawSize` | 原始大小。**`0xFFFFFFFF` = whiteout**(見 §4.3) |
| `0x10` | `u64` | `dataOffset` | 相對 `header.dataOffset` |
| `0x18` | `u32` | `storedSize` | 在資料區佔用的 bytes |
| `0x1C` | `u16` | `compression` | 0 = store,1 = deflate |
| `0x1E` | `u16` | `cryptRange` | 0 = 不加密,1 = 全檔,2 = 只前 4096 bytes |
| `0x20` | `u32` | `crc32` | 原始資料的 CRC32 |
| `0x24` | `u32` | `reserved` | 0 |

**pathHash 碰撞**:64-bit 雜湊對 10 萬條路徑的碰撞機率約 `2.7e-10`,可忽略,但
**打包器必須在打包時檢查碰撞並直接失敗**,絕不能靜默帶過 —— 靜默帶過的後果是某個資產
永遠讀到另一個檔的內容。讀取端(`PakProvider.IndexOf`)還會再比對一次真正的路徑字串當最後防線。

> 用 FNV-1a 而不是 xxHash64:C# 與 Python 兩邊各五行就寫得完、不會寫錯,Python 端零依賴。
> 碰撞既然本來就硬檢查,雜湊品質不是瓶頸。

### 3.3 資料區

每筆條目獨立壓縮、獨立可解密 —— 這是能隨機存取的前提。條目之間不共用壓縮字典。

---

## 4. 掛載與解析

### 4.1 路徑正規化

VFS 對外的路徑一律是「相對 `DATA/` 的正規化路徑」:

1. `\` → `/`
2. 去掉前導 `./` 與 `/`
3. 摺疊 `.` 與 `..`;若摺疊後逃出根 → **視為無效路徑,回 null**(不是拋例外)
4. 去掉結尾 `/`

雜湊鍵另外算:對正規化路徑做 **ASCII-only 的大寫轉換**(只把 `a`–`z` 轉 `A`–`Z`,
非 ASCII byte 原樣保留)再取 **FNV-1a 64**。

> ASCII-only 是刻意的。原始資料樹是純 ASCII 檔名,而 NTFS 大小寫不敏感,程式碼裡對同一個檔
> 大小寫混用。用 `ToUpperInvariant()` 會踩到土耳其語 `i`/`İ` 之類的 locale 陷阱,而
> `ADDON/` 底下全是玩家的 unicode 檔名 —— 那些走真實檔案系統,根本不進雜湊表。

### 4.2 掛載順序

由低到高:

| 優先權 | 來源 | `pakId` | 說明 |
|---:|---|---|---|
| `100 + pakId` | `base_*.pak` | 10–19 | |
| `100 + pakId` | `music_*.pak` | 20+ | |
| `100 + pakId` | `patch_NNN.pak` | 300+ | 數字大者更高 |
| `1000` | `DATA/` 底下的**真實檔案** | — | 開發覆寫 / 熱修 / mod:丟一個 `DATA/AVATAR/xxx.dds` 就蓋掉 pak 內的 |
| 硬隔離 | reserved 目錄 | — | `PROFILE` `ADDON` `CACHE` `REPLAY` —— 見下 |

查檔時**從最高層往下找第一個命中**。

**優先權完全由卷自己的 `pakId` 決定,不看檔名** —— 改名不會改變覆蓋關係。
`SdoVfs.Initialise` 開機時掃 `DATA/*.pak`、逐個 `PakProvider.TryOpen`,
用 `PriorityPakBase + pakId` 掛上去;`pakId` 的發號規則在
[`build_pak.py`](../../tools/build_pak.py) 的 `VOLUMES`。
開不起來的卷(壞檔、被改過、版本不符)**安靜跳過** —— 一個壞卷不該讓整個遊戲開不起來,
少一層頂多是某些資產讀不到。

**reserved 目錄不參與 pak 解析**:正規化路徑的第一段若是這四個之一,直接走真實檔案系統,
完全不查任何 pak。打包器也必須把這四個前綴列為排除項,永遠不打包進去。

editor 下的 loose 資料樹(`assets/sdox_offline/Extracted`)天然就是第 4 層 ——
那棵樹底下沒有任何 pak,所以全部命中真實檔案。這表示**現有那一大票直接讀真實 DATA 路徑的
EditMode 測試一行都不用改**。這是設計硬要求,不是附帶效果。

### 4.3 whiteout

patch 卷要能「刪掉」東西。`rawSize == 0xFFFFFFFF` 的條目表示該路徑已被移除:
解析時命中 whiteout 就**停止往下層找,回報不存在**。

### 4.4 列舉

`EnumerateFiles(dir, pattern, recursive)` 要合併各層結果:高層覆蓋同名、whiteout 移除、最後去重。
pak provider 的路徑表保持排序,所以按前綴做二分搜尋取範圍即可,不必掃全表。

---

## 5. 加密

### 5.1 金鑰

```
masterKey  = SHA-256(seg0 ‖ seg1 ‖ seg2 ‖ seg3)     四段散在不同編譯單元，執行期組合
dataKey    = HKDF-SHA256(masterKey, salt=magic, info="sdopak:data:" + pakId)[0..16]
indexKey   = HKDF-SHA256(masterKey, salt=magic, info="sdopak:idx:"  + pakId)[0..16]
macKey     = HKDF-SHA256(masterKey, salt=magic, info="sdopak:mac:"  + pakId)[0..32]
```

### 5.2 資料區:單一 CTR 串流

**整個資料區視為一條 AES-128-CTR 金鑰流**,counter block = `byteOffsetInDataRegion / 16`。

這樣做的理由:CTR 模式最致命的錯誤是同金鑰重用 counter(金鑰流重用 = 直接破)。
用「在資料區中的絕對位移」當 counter 起點,條目之間不重疊 ⇒ 金鑰流永不重複,
而且仍可隨機存取:要讀位移 `O` 的條目,從 counter `O/16` 起算、跳過 `O%16` bytes 即可。

`cryptRange == 2`(表頭加密)只 XOR 該條目的前 4096 bytes,其餘明文 —— 一樣不造成重用。

### 5.3 索引區

`indexKey` + AES-128-CTR(counter 從 0),外加 `HMAC-SHA256(macKey, 密文)` 取前 16 bytes 放檔頭。

> HMAC 的金鑰同樣在執行檔裡,所以它只擋「改了檔沒重簽」,擋不住有心人重簽。
> 條目層級的完整性靠 `crc32`,那是防損毀不是防竄改。

### 5.4 效能

Unity Mono 的 AES 是 managed 實作(沒有 AES-NI),約 100–200 MB/s。單一資產都是幾十 KB,
不會是瓶頸;音訊靠表頭加密繞開。若日後量到 AES 真的成為熱點,再換 ChaCha8 或純 XOR 金鑰流
——格式已經用 `flags` 留了空間。

---

## 6. 實作對照

| 一半 | 另一半 | 內容 |
|---|---|---|
| [`PakFormat.cs`](../../65/My%20project/Assets/Scripts/Sdo.Settings/Vfs/PakFormat.cs) | [`sdopak.py`](../../tools/sdopak.py) | 二進位版面 + CRC32 |
| [`PakCrypto.cs`](../../65/My%20project/Assets/Scripts/Sdo.Settings/Vfs/PakCrypto.cs) | 同上 | 金鑰派生 + AES-CTR + HMAC |
| [`PakProvider.cs`](../../65/My%20project/Assets/Scripts/Sdo.Settings/Vfs/PakProvider.cs) | — | 讀(執行期唯一用到的) |
| [`PakWriter.cs`](../../65/My%20project/Assets/Scripts/Sdo.Settings/Vfs/PakWriter.cs) | `sdopak.PakWriter` | 寫(記憶體內;只給測試用) |
| — | `sdopak.PakBuilder` | 寫(串流;正式打包,4 GB 的卷不能塞記憶體) |

**這兩份是同一個契約的兩半,改一邊就要改另一邊,而且要昇版號。**

### 跨語言契約怎麼驗

**不是**比對 byte 完全一致 —— C# 的 `DeflateStream` 與 Python 的 `zlib` 對同一份輸入會產生
**不同但都合法**的 deflate 位元流,永遠對不起來。要驗的是「C# 讀得懂 Python 產的檔」:

- `tools/tests/test_sdopak.py` 產一個涵蓋所有特性的 fixture(store / deflate / 全檔加密 /
  表頭加密 / whiteout / 中文路徑)→ `Assets/Tests/EditMode/Fixtures/contract_v1.pak.bytes`
- C# 的 `PakTests.ReadsPythonProducedPak` 讀它並逐項驗內容

兩邊一漂移,那個測試就紅。fixture 是 deterministic 的,重新產生不會製造假 diff
(`python tools/tests/test_sdopak.py --write`)。

---

## 7. 打包器

`tools/build_pak.py`,由 [`package_build.ps1`](../../tools/package_build.ps1) 的 `-Pack` /
`-Encrypt` 開關呼叫(預設關 —— 開發時散裝樹好查、改一個檔就生效)。要求:

- **由 manifest 決定分卷**:哪些目錄進哪一卷、每卷的壓縮與加密策略
- **輸出必須 deterministic**:同輸入 → 同 bytes。條目依 `pathHash` 排序、時間戳一律歸零。
  這是產 patch diff 的前提。
- **產 patch 卷**:比對舊 manifest,只放變動與新增的檔,消失的檔寫成 whiteout
- **pathHash 碰撞 → 直接失敗**
- **reserved 前綴(`PROFILE` `ADDON` `CACHE` `REPLAY`)一律排除**
- 每卷輸出一份 `.manifest.json` 側車檔(路徑 → 大小/CRC),供下次做 diff 與驗證

---

## 8. 已知的坑

### 8.0 遷移到 VFS 時最容易漏的兩個樣式

批次替換 `File.Exists` / `File.ReadAllBytes` / `Directory.GetFiles` 這些常見寫法很直覺,
但下面兩個**長得不一樣、後果卻最嚴重**,已經踩過:

| 漏掉的寫法 | pak 化之後 |
|---|---|
| `new FileStream(abs, …)` | 對 pak 內的條目**直接丟例外**。呼叫端多半包了 `try/catch`,於是被靜默吞掉 —— 程式看起來跑完了,其實什麼都沒讀到 |
| `Directory.EnumerateFiles(dir, …)` | 對只存在於 pak 裡的目錄回**空集合**,整棵樹被靜默跳過 |

兩者的共同點是**不會報錯**。`UsedAssetsProbe` 同時中了這兩個:pak 版的死檔探測
「13.7 秒跑完、touched=2231 / missing=43010」,看起來很正常,實際上一個檔都沒讀 ——
還讓 pak vs 散裝的效能比較完全失真。

檢查方式:

```bash
grep -rn "new FileStream(\|Directory.EnumerateFiles(" --include=*.cs Assets/Scripts | grep -v "Sdo.Settings/Vfs/"
```

命中的位置若是在 reserved 區(`PROFILE` / `ADDON` / `CACHE` / `REPLAY`,以及外部歌曲資料夾)
就可以留著走原生 IO —— 那些本來就不進 pak。


1. **音訊的 `file://` 路徑**。`UnityWebRequestMultimedia` 要真實路徑,pak 內的檔沒有。
   MUSIC 進 pak ⇒ 音訊必須全面改走記憶體解碼。專案已有吃 `byte[]` 的
   `MadDecoder` / `Mp3Decoder` 路徑,可行,但**每一條音訊路徑都要確認過**,
   包含選歌試聽、BGM、keysound。這塊剛修好([`sdo-mp3-gapless-sync`]),要小心回歸。
2. **`SdoDataRoot.LooksLikeGameDataRoot`**。它現在靠「有沒有 `AVATAR/FEMALE.HRC`、`3DEFT/`、
   `SCENE/`」判斷這是不是資料樹。pak 化後那些路徑在磁碟上不存在了,判準要加一條
   「或有 `base_core.pak`」,否則 `PickRoot` 會整個認不出 DATA。
3. **靠掃資料夾的邏輯**:`FuzzyFindDds`、`DressCatalog`、`AvatarItemCatalog`、
   `ExternalSongScanner`、`UsedAssetsProbe` 全部要改走 VFS 列舉。這也是為什麼索引一定要帶
   完整路徑表,不能只存雜湊。
4. **`AvatarAssetCache` 的背景預讀要重寫**。pak 化後「冷開檔」不再是主要成本,
   現有的預讀策略可能變成純浪費。

---

## 9. 落地順序

每一步結束時專案都必須是可 build、測試全綠的狀態。

1. **`SdoVfs` + loose provider**,275 個直接 IO 呼叫點分批遷移。行為零變化。
2. **不漏檔案**(§1.1)。可獨立驗證:清空那三個外部路徑跟 registry key,跑一輪,確認沒有新東西冒出來。
3. **pak reader/writer + 單元測試**(round-trip / 覆蓋 / whiteout / 損毀偵測)。
4. **`build_pak.py`** + 接進 `package_build.ps1`。
5. **分層掛載 + patch 卷產生器**。
6. **加密**。最後才上 —— 它是最容易加的一層,先別讓它擋住前面的驗證。
