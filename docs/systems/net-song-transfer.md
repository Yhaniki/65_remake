# 缺歌自動傳檔(Song Transfer)

房主選了一首**外部歌**(osu/StepMania 匯入的),而別人沒有 → 自動從 server 下載,
下載完直接接進歌庫**不用重開遊戲**。官方歌不走這條(大家的 `DATA/MUSIC` 是同一份)。

## 歌曲在網路上的身分:`packId`

```
packId = "sha256:" + <manifest 的 SHA-256 前 32 個 hex 字>
manifest 每行 = <相對路徑,小寫,'/'> \x1F <位元組數> \x1F <譜面才有的 SHA-256>
          → Ordinal 排序 → '\n' 串起
```

🔴 **不能用外部歌的 `gn`**:那是 `"ext_" + FnvHex(絕對路徑)`,換台電腦完全不同。
`fileId` 也是掃描索引,同理。用它們比對「你有沒有這首歌」永遠不會對。

**音檔不算 SHA-256**,只進(檔名, 長度)。這是刻意的取捨:大歌庫幾 GB,開機掃描不可能讀完。
而 (檔名, 長度) 對「這是不是同一個資料夾」已經足夠強,**下載完每個檔還會逐一重算 SHA-256 驗證**。
代價寫在下面的「已知界線」。

`packId` 掛在 `ExternalScanCache` 上(它的 `Signature` 本來就是 file-stat token,失效條件完全一致),
所以不會每次開機重算。

## 哪些檔案會傳

| 類別 | 規則 |
|---|---|
| 白名單 | `.osu .sm .gn .mc .tsv .ogg .mp3 .wav .png .jpg .jpeg .bmp .dds .osb .ini` |
| 客製編舞 | `.dps .mot .cdt` —— 使用者自己放進歌曲資料夾、sidecar `#DPS`/`#MOT`/`#CAMERA` 指名的那些 |
| 影片(需求) | 全部排除:`.mp4 .avi .flv .wmv .mkv .mov .webm .mpg .mpeg .m4v .ts .rmvb .asf .ogv .3gp` |
| 執行檔/壓縮檔 | 排除 `.exe .dll .bat .cmd .sh .ps1 .msi .scr .lnk .com` / `.zip .rar .7z .osz .osk` |
| 生成物 | 排除 `cd*.png` / `dance*.dps` —— 收端自己會重生 |
| 編輯器備份 | 排除整個 `FileBackup/` —— StepMania/ArrowVortex 的自動存檔,見下 |
| companion | `sdoinfo.dat`(與舊名 `sdo.header`)**傳,但不進 packId** —— 見下 |
| 大小 | 單檔 > 32 MB 排除(擋「改名成 .ogg 的影片」);圖 > 4 MB 排除;整包 > 200 MB → `tooBig` |
| 深度 | 歌曲資料夾本身 + **一層**子夾 |
| 路徑 | 全部過 `SafeRelPath.IsSafe`(`..` / 絕對 / 磁碟機 / UNC / 控制字元 / `CON`·`NUL`·`COM1`) |

過濾規則是**純函式**(`Sdo.Osu/SongPackFilter.cs`),client 與 server 跑同一份。

### 🔴 客製編舞與側車檔:要傳,但只有一個進 packId

歌曲資料夾可以自帶**作者編的舞**:`#DPS:12951.DPS;` 指到自己的 .dps,它點名的 `WDANCE*.MOT` 也躺在同一個
資料夾(見 `ExternalDps` 與 `ScreenGameplay.TryLoadMotFromSongFolder`)。這一整組必須跟著過去 ——
不然同一場裡房主跳的是作者編的,下載到這首歌的人跳的是亂數生成的那一支。

要讓它真的生效,**兩樣缺一不可**:

- **檔案本身**(`.dps` / `.mot` / `.cdt` / 客製 CD 圖)進白名單,而且**算進 packId** ——
  換一支編舞就是換一份歌,對方該重新下載。
- **`sdoinfo.dat` 本身**要傳(它是「用哪一支編舞、哪張碟、offset 校到多少」的唯一指標:
  檔案到了收端卻沒人指它,收端照樣生一份自動編舞),但**不能算進 packId** ——
  它是**執行期會被改寫的**檔(第一次選歌寫 `#CDIMAGE`、第一次玩寫 `#DPS`/`#DPSVER`)。
  算進身分的話,送端玩過一次自己的 packId 就變了(server 上那包再也對不上),
  收端下載完玩一次也一樣(「我明明有這首歌」被判定成沒有,每次回房重載一次)。

這就是 `PackFileVerdict.Companion`,與模型包的 `physics.ini` 同一個機制(`ModelPackFilter`)。
`BuildManifest` 跳過 companion,`ScanFolder`/`Enumerate`/`IsTransferable` 收下它。

指標指到沒傳過去的東西是**安全的降級**:`#CDIMAGE:cd.png` 的檔不在 → `ExternalCdImage` 重新合成一張;
`#DPS:dance.dps` 的檔不在 → `ExternalDps` 用同一個 seed 重生成一支一樣的(見下)。

### 🔴 `FileBackup/` 整個排除,而且不算進 packId

StepMania / ArrowVortex 每存一次譜就往 `<歌>/FileBackup/` 丟一個帶時間戳的 `.sm`(實測一首編過幾輪的歌
躺著 24 個)。那是**編輯歷史,不是這首歌**:

- 算進 packId 的話,**每存一次譜這首歌就換一個身分** → 房裡每個人都得重下一遍一份只多了備份檔的相同歌曲,
  而畫面上完全看不出為什麼。
- 傳過去也只是浪費頻寬與磁碟,而且備份多的歌會往 `MaxPackFiles`(600)那條上限逼近。
- 收端拿到更糟:那些 `.sm` 旁邊沒有音檔,`ExternalSongScanner` 本來就刻意不把它當成一首歌,只會佔磁碟。

判定是 `SongPackFilter.IsEditorBackup`(看相對路徑的第一段,不分大小寫)。

🔴 **遊戲自己生的 `dance*.dps` 不傳,但重生出來的必須是同一支舞。**
(歌曲**自帶**的客製 .dps 是另一回事 —— 那個會傳,見上面一節。)收端自己跑 `Sdo.Game.ExternalDps` 生一份,
而它的 RNG seed 是**三張難度譜的 SHA-256(當成集合)**,其他一概不看 ——

- **不能是資料夾名**:下載端的資料夾叫 `歌名 - 作者 [packId 前8碼]`,上傳端叫什麼根本沒在協定裡傳
  → 兩邊 seed 不同 → xorshift 第一抽就分岔 → 同一場的兩個人跳完全不同的舞(這是實機抓到的 bug)。
- **也不吃檔名/音檔/圖**:譜面是清單裡唯一逐位元組驗過 SHA-256 的東西,也正好是舞蹈長度/BPM 的唯一來源
  (`Sdo.Osu/DanceInputs.cs`)——「會改變舞蹈的東西全在 seed 裡,不會改變舞蹈的東西全不在」。
- **不能吃槽順序**:哪張譜排進簡單/普通/困難由每台自己的 `RoomConfig.difficultyCalc`(minacalc / osu)決定,
  兩個人手上同樣三張譜、槽的順序卻可能不同 → 指紋必須排序當集合。
- 一張譜都讀不到才退回 `packId` + `songKey`,再退回資料夾葉名。

改到編舞的生成邏輯時,`SongSidecar.DpsGenerator` 要跟著加一號,否則已經跳過的歌會沿用舊 seed 生的那份。

## server 的存放方式:內容尋址

```
<dataDir>/blobs/files/<sha[0:2]>/<sha>    檔案本體(去重)
<dataDir>/blobs/packs/<packId hex>.json   這首歌由哪些檔案組成 + 最後使用時間
<dataDir>/blobs/tmp/<uploadId>/           上傳暫存
```

為什麼是這樣(評估過的替代方案):

- ❌ **P2P 直傳**:NAT/防火牆在混用環境不可靠。
- ❌ **server 純轉發不落地**:房主必須全程在線,遲到的人要重觸發上傳,還要處理 backpressure ——
  複雜度換來的只是省磁碟。
- ✅ **內容尋址 + TTL**:同一首歌**第二次有人玩就零上傳**;per-file 去重 → 改一張譜重傳幾乎瞬間
  (音檔/圖早就在了),兩個共用同一音檔的 beatmap set 只存一份。

🔴 **pack 的檔名要去掉 `sha256:` 前綴。** Windows 上冒號不是「不合法字元然後報錯」,而是
**NTFS alternate data stream 的分隔符** —— `packs/sha256:abcd.json` 會安靜地寫成
`packs/sha256` 的隱形附屬串流:寫入成功、列不出來、重開 server 之後所有包都「消失」。

## 流程

```
房主 setSong(外部歌)
  └─ blobUploadBegin(manifest) ──> blobUploadAccept{need:[還缺的 index]}
       need 是空的 → 直接完成(同一首歌第二次有人開 = 零上傳)
       否則逐檔 64 KiB chunk ──> blobUploadDone ──> 房內廣播 blobAvailable

其他人收到 roomState.song
  ├─ 有 → setAvailability(have)
  └─ 沒有 → setAvailability(missing) + 頭貼那條徽章換成「NO MAP」(c06..c09)
       座位玩家 + netAutoDownload → blobQuery(有嗎?)→ 有才 blobDownloadBegin
       旁觀者 → **不自動下載**(需求)
       下載 → 寫 <DATA>/ADDON/SONG/connect/<歌名 - 作者 [packId 前8碼]>/
            → 每 500ms 回報進度(頭貼下方的跑條)
            → 逐檔比對 SHA-256 → 重新掃描歌庫 → 用 packId 再確認找得到 → have
```

**下載目的地是 `ADDON/SONG/connect/`。** 這個選擇剛好與掃描器的語意吻合:
`ExternalSongScanner` 把 root 底下一層當「分類」→ `connect` 會自然成為選歌畫面裡的一個分類,
所有從別人那裡下載來的歌都歸在那一格,一眼找得到、要清也好清。

資料夾名用「歌名 - 作者 [packId 前 8 碼]」而不是原本的資料夾名:原名根本沒在協定裡傳
(manifest 只有相對路徑),而**一律**加上 pack tag 讓撞名問題直接消失。
資料夾名不影響 packId(它只看相對路徑與內容)。

## server 絕不信任上傳者

每一步都自己重算一次:

1. 每個相對路徑過 `SafeRelPath` + `SongPackFilter`
2. **整份清單重算 packId**,對不上整批不收 ——
   否則上傳者可以宣稱「這包是別人那首熱門歌」然後把內容換掉
3. 每個檔案收完**自己重算 SHA-256**(不信宣稱的 hash)
4. 只能上傳「自己房間現在選的那首歌」—— 否則任何連上來的人都能把 server 當免費檔案空間
5. **已經存在的 packId 不覆寫**,只更新使用時間(見下面的「已知界線」)

## 定期清理(需求:最多留一天)

`BlobJanitor` 每 15 分鐘:
1. **pin**「被存活房間當前歌引用」的 packId
2. 未 pinned 且 `now - lastUsedUtc > 24h` → 刪 pack json
3. 用**剩下的**包重算引用計數 → 計數 0 的檔案才刪
4. 總量超過上限 → 從最久沒用的未 pinned 包開始丟

順序很重要:先決定哪些**包**要走,再重算引用計數。反過來(先掃檔案)會刪掉還被別的包引用的
共用音檔,而症狀是「別人下載完譜面對不上」,完全指不到清理邏輯。

🔴 **不依賴檔案系統的 atime。** Linux 上 `noatime` 是很常見的掛載選項,那時
`LastAccessTime` 根本不會動 → 清理邏輯會以為每個包都「剛用過」而永遠不刪。
`lastUsedUtc` 明確寫進 pack json,每次 upload/download/query 命中就更新。

🔴 **有上傳進行中就整輪跳過。** 上傳是「一個檔一個檔 commit,最後才寫 pack json」,
那段時間裡已收好的 blob 還沒有任何 pack 引用它們 → 會被當孤兒刪掉,整份上傳白做。

決策邏輯在 `Sdo.Net/Server/BlobIndex.cs`(純函式,注入時鐘)→ 可以直接單元測試,不用碰磁碟。

## 已知界線(刻意接受的)

| 界線 | 為什麼接受 |
|---|---|
| 音檔只比(檔名, 長度),不比內容 | 開機掃描不可能讀完幾 GB。理論上可以做出「譜一樣、音檔長度一樣但內容不同」的包 → packId 相符。防線是**已存在的 pack 不覆寫**:第一份先到的那份就是大家拿到的那份。 |
| 沒有斷點續傳 | 傳輸中斷就整份重來(而且半成品資料夾會被刪掉 —— 留著會被下次掃描當成一首正常的歌收進目錄,那比重傳糟得多)。一首歌通常幾 MB,重來的成本可接受。 |
| 目錄說有歌還要檔案真的在 | 掃描快取可能是舊的(玩家把歌刪了、或同機兩開共用 `persistentDataPath` 的快取)。謊報 have 的後果是被納入這一場然後載不到譜,全房等逾時 —— 所以會多做一次 `File.Exists`。 |

## 相關

- [net-protocol.md](net-protocol.md) —— `blob*` 訊息的欄位
- [networking.md](networking.md) —— 為什麼傳檔走第二條連線
- [beatmap-import.md](beatmap-import.md) —— 外部歌怎麼被掃進來
