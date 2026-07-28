# 連線協定(Net Protocol)

`Sdo.Net`(client 與 server **編同一份原始碼**)。這份文件講 wire format、訊息集合、
以及 server 那 21 條房間規則。

## Wire format

```
[uint32 LE payloadLen][byte kind][payload]
kind 0 = UTF-8 JSON      kind 1 = 原始位元組(檔案 chunk)
```

**為什麼 kind 要存在**:同一條 TCP 上要混跑 JSON control 訊息與檔案位元組。
把二進位 base64 塞進 JSON 會多 33% 流量而且大量產生字串垃圾(一首歌幾十 MB 很有感),
所以 chunk 走 kind=1 直接送 raw bytes,兩種 frame 共用同一個 reader。

payload 上限 **256 KiB**。🔴 **上限檢查必須在 allocate 之前** ——
少了它,對方送一個 `length = 0xFFFFFFFF` 的 header(壞掉的 client 或惡意封包)
就能讓我們試圖配置 4 GB 直接倒掉。header 的編解碼刻意做成純函式
(`NetFrame.WriteHeader` / `TryParseHeader`):client 是背景 thread 的同步讀取、
server 是 async 讀取,兩邊 IO 寫法不同但**驗證邏輯必須一模一樣**。

## 序列化

`Sdo.Osu/MiniJson.cs` 解析 + 自己寫的 writer(`NetJson` / `JObj` / `JArr`),**零多型零反射**。

- Unity 的 Mono 沒有 `System.Text.Json`(不在 netstandard2.1)。
- osu 的 `SignalRWorkaroundTypes.cs` 手維護 40 幾筆 `[Union]` tag 是明確的反面教材 →
  這裡 `{"t":"joinRoom"}` 的 `t` 就是唯一的 dispatch key。
- ⚠️ MiniJson 會**靜默失敗**(`Parse` 回 null、`ParseNumber` 回 0.0)。所以
  `NetJson.TryParse` 一律把那些當 protocol error,不是「拿到空物件繼續跑」。

## 兩條連線

| role | 用途 |
|---|---|
| `control` | 房間狀態、聊天、分數流。握手後 server 發一個 `sessionKey`。 |
| `file` | 檔案傳輸。用 control 那條發的 `sessionKey` 認親,同一個 port。 |

**為什麼分開**:一首歌幾十 MB 的 chunk 會排在房間訊息前面 →
整個房間在傳檔期間看起來像卡住(聊天不出現、別人準備了看不到)。

⚠️ file 連線一樣要:(a) 帶進站密碼(server 對**每一條**連線都檢查);
(b) **定期 ping**(server 的 15 秒斷線掃描看的是「多久沒收到東西」,而下載時我們一路只收不送 →
不 ping 的話任何超過 15 秒的下載都會被砍掉)。

## 訊息集合

| 類別 | 訊息 |
|---|---|
| 連線 | `hello`{proto,role,playerId,name,gender,level,guild,password?,**authToken?**,sessionKey} → `welcome`{userId,sessionKey,capacity,fileTtlHours,maxBlobBytes} / `bye`{reason} / `ping`·`pong`{t0} —— **5 秒一次,15 秒沒收到 = 斷線 = 離房** |
| 房間 | `roomList` / `createRoom`{mode,name} / `joinRoom`{code} → `joinResult`{ok/full/inGame/notFound} / `leaveRoom` / **`roomState`**{rev,code,name,hostUserId,mode,status,capacity,seats[…],spectators[],song,settings} / `setRoomName` |
| 座位 | `kickUser` / `setSeatClosed` / `transferHost` / `kicked`{reason} / `error`{rq?,code,msg} |
| 組隊 | `assignTeams`{layout:"2v2"/"3v3"/"2v2v2"} / `setOwnTeam`{team:0..3} |
| 開場 | `setReady` / `setSong`{NetSongRef} / `setRoomSettings` / `requestStart`{force,resolved} → `matchStarting`{matchId,startEpochMs,loadTimeoutMs,participants[],spectatorNames[],resolved,song,settings} / `setPlayState` / `gameplayStarted` / `gameplayAborted` / `resultsReady`{rows[]} |
| 缺歌 | `setAvailability`{packId,state,progress} / `blobQuery`→`blobInfo` / `blobUploadBegin`→`blobUploadAccept`{need[]}→(chunks)→`blobUploadDone` / `blobProgress` / `blobDownloadBegin`→`blobManifest`→(chunks)→`blobDownloadDone` / `blobAvailable` / `blobError` |
| 分數流 | `frame`{matchId,tMs,score,combo,maxCombo,hp,p,c,b,m} C→S / `frames`{f:[…]} S→C(**server 攢所有人最新一筆固定 5 Hz 推一次** → N 人下行 N×5 而不是 N²) / `playFinished` |
| 房間走動 | `move`{x,z,facing,walking} C→S / `moves` S→C(同上,但頻率高一點 —— 位置是連續量) |
| 外觀 | `setLook`{gender,bodyIndex,parts[]} —— 握手時玩家還沒選性別/還沒讀 profile,所以外觀要另外送 |
| 旁觀 | `spectate`{code} / `stopSpectate` |
| 聊天 | `chatSay` / `chatMsg` / `announce` |

`playState`:`idle` `ready` **`waitingForLoad`** `loaded` `readyForGameplay` **`playing`** `finished`
**`results`** `spectating`(粗體 = server 保留,client 送不進來)。

### 握手時的身分:`authToken`

server 沒給 `--tokens` 時 `authToken` 被忽略,身分 = client 自稱的 `playerId`/`name`(LAN 模式)。

給了 token 檔之後**server 說了算**:查得到就用 token 綁的 `playerId`/`name` 覆蓋 client 自稱的那組,
查不到 → `bye{badToken}`。所以「把 hello 的 playerId 改成別人的」在啟用 token 之後不再有效。

同一條連線上還有另外兩道在 hello **之前**就生效的門(連線在握手之前已經成立):
來源允許名單 → `bye{notAllowed}`、per-IP 連線數上限 → `bye{tooManyFromIp}`。

加密不在協定層:TLS 包在 framing 外面(`[len][kind][payload]` 一個 byte 都沒變),
所以 `serverTls` 只影響 stream 怎麼建起來。憑證釘選規則見 `Sdo.Net.TlsPinning`。

### 為什麼推整份 snapshot 而不是 delta

6 人 × 約 1 KB 不是問題,而它消滅一整類「兩邊狀態慢慢漂開」的 bug。`rev` 單調遞增。
**而且不做樂觀更新**:按了按鈕不改本機畫面,等 server 的下一份 snapshot ——
server 會拒絕(不是房主、房間開打了、座位滿了),樂觀更新會顯示一件沒發生的事。

### 歌曲參照 `NetSongRef`

```
official=true  → gn + fileId(官方歌:全球唯一且穩定)
official=false → packId + songKey + chartRelPath + chartIndex(見 net-song-transfer.md)
顯示用          → title / artist / bpm / level / noteCount / durationSec / difficulty
randomTitle    → title 是「隨機難度 X」的標籤,收端不要拿 gn 去查目錄(那等於提前揭曉)
```

🔴 `chartRelPath` 是**相對歌曲資料夾**的路徑,不是本機絕對路徑 ——
`GameSession.ExternalChartPath` 存的是絕對路徑,直接塞進去會被 server 的
`SafeRelPath.IsSafe` 擋掉(它不收磁碟機代號)→ 整個 `setSong` 回 `badState`,
而畫面上只是「選了歌但房間沒歌」。(實機驗證抓到的。)

## 房間規則(每一條都有一個測試)

| # | 規則 |
|---|---|
| R1 | 房號 `10000..99999`,洗牌池 O(1) 配發、關房回收(不用 `Random.Next` 重試) |
| R2-R3 | `capacity=6`;join → index 最小的 `Open` 座位,沒有 → `full` |
| R4 | host = `hostUserId`,**不是 seat 0** |
| R5 | host 離開 → 轉給剩下座位 index 最小者;沒有座位玩家 → `hostUserId=0`(無房主);**房間只在一個人都不剩時關閉**(旁觀者算人) |
| R5b | **第一個坐上座位的人自動成為房主**;無房主期間所有 host-only 操作回 `notHost` |
| R6 | 斷線 == `leaveRoom`,idempotent |
| R7 | host-only 清單(見 room-matchmaking.md)。非 host → `error{notHost}`,**絕不靜默忽略** |
| R8 | 關自己那格 → `badSeat`;**關閉已有人的座位 → 先 `kicked{seatClosed}` 再標 Closed** |
| R9 | `setSong` → 清全員 ready、全員 `avail=unknown`、`rev++` |
| R10a | `setOwnTeam` 需 `playState==idle` 且未按準備(房主的 Ready 恆 false 所以它一直能換隊) |
| R10b | `assignTeams` 驗座位玩家人數符合 layout,否則 `badTeams` |
| R10c | **`requestStart` 在組隊模式下必須湊出 2+2 / 3+3 / 2+2+2,否則擋住**(含 `force`)。參與者集合是「ready 且 `avail==have`」的座位 |
| R11 | `lookerCount` 縮小到低於現有旁觀人數 → 踢掉最新加入的旁觀者 |
| R12 | `open → waitingForLoad`:**參與者在這一刻凍結** = (是房主 或 已準備)且 `avail==have`;非參與者留在房間 |
| R13 | `waitingForLoad → playing`:沒人還在 `waitingForLoad` 且 ≥1 人 `loaded`;`loaded` 集合為空 → 退回 `open` + `gameplayAborted{noParticipants}` |
| R14 | `playing → results → open`:沒人還是 `playing` → 廣播 `resultsReady` → 回 `idle` |
| R15 | **載入 timeout 30 秒**:還在 `waitingForLoad` → 逐出本場;卡在 `loaded` → **強制轉 `playing`** |
| R16 | 遊玩中斷線 → 逐出本場,最後一筆 frame 仍列入結算(`disconnected:true`);host 斷線 → **本場繼續** |
| R17 | `setReady` 需 `avail=="have"`;ready 中的人 `avail` 翻成 `missing` → **自動取消 ready** |
| R18 | join 一個 `status != open` 的房間 → `inGame`;**但 `spectate` 允許** |
| R19 | rate limit:control 32/s、`frame` 20/s、chat 5/3s、`setAvailability{downloading}` 1/500ms。超過丟訊息;持續超過 → `bye{rateLimit}` |
| R20 | 上限:maxRooms 200、maxConnections 256、name ≤16、roomName ≤20、blob 總量 20 GB |
| R21 | 旁觀切換的三道門(見 room-matchmaking.md) |
| D15 | `setReady` 只在 `status==open` 時允許 —— 房間在 `waitingForLoad`/`playing` 期間任何人都按不了準備 |

## 測試

`dotnet test server/` —— 秒級,不用開 Unity(`Sdo.Net` / `SongPackId` / `SongPackFilter` /
`SafeRelPath` 全部零 Unity 依賴)。

| 測試檔 | 守什麼 |
|---|---|
| `NetRoomRulesTests` | **R1..R21 每條一個測試** —— 全案最重要的測試檔 |
| `NetFrameTests` | round-trip / 截斷 / **超長 length 必須在 allocate 前被拒** / kind 混流 |
| `NetJsonTests` | escaping / InvariantCulture 小數點 / `Long` 精度到 2^53 / **壞 JSON 必須 fail 而不是回空物件** |
| `ServerIntegrationTests` | **真的開 socket**:framing、握手、dispatch、廣播對象、actor marshalling |
| `BlobTransferTests` | **真的傳位元組**:逐位元組比對 + 每一條「不信任上傳者」的防線 |
| `BlobIndexTests` / `DiskBlobIoTests` | 清理決策(注入時鐘)與磁碟層 |
| `TeamLayoutRulesTests` | `{2,2}`/`{3,3}`/`{2,2,2}` 合法;`{3,2}`/`{4,1}`/含自由 全部擋住 |
| `SongPackIdTests` / `SongPackScanTests` | 搬路徑/改大小寫 → 同 id;加刪影片 → id 不變;改譜 → id 變 |

🔴 **單元測試全綠不代表接線是對的。** M5 的實機驗證連續七輪各抓到一個不同的真問題
(絕對路徑進協定、連線被自己關掉、file 連線沒帶密碼、斷線檢查搶在讀 bye 之前、
守門放錯位置…),而那七個當下**單元測試都是全綠的** —— 它們是「接線」錯誤,不是邏輯錯誤。
所以協定層的改動一律要用 `tools/shoot_room_bubble.ps1` 兩開實機跑一次。

## 相關

- [networking.md](networking.md) —— 傳輸層、執行緒模型、診斷、安全性
- [room-matchmaking.md](room-matchmaking.md) —— 房間/座位/開場的行為面
- [net-song-transfer.md](net-song-transfer.md) —— 缺歌傳檔
- `server/README.md` —— 怎麼 build / 部署 / 多開測試
