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
| 連線 | `hello`{proto,role,playerId,name,gender,level,guild,**guildEmblem**,**build**,password?,**authToken?**,sessionKey} → `welcome`{userId,sessionKey,capacity,fileTtlHours,maxBlobBytes} / `bye`{reason} / `ping`·`pong`{t0} —— **5 秒一次,15 秒沒收到 = 斷線 = 離房** |
| 房間 | `roomList` / `createRoom`{mode,name} / `joinRoom`{code} → `joinResult`{ok/full/inGame/notFound} / `leaveRoom` / **`roomState`**{rev,code,name,hostUserId,mode,status,capacity,seats[…],spectators[],song,settings} / `setRoomName` |
| 名單 | `userList` → `userListResult`{users:[{userId,name,guild,**guildEmblem**,level,gender,**roomSeq**}]} —— 大廳左側玩家名單(全部/好友/家族)的資料來源。**沒有上下線推播**,與 `roomList` 同一個問答模式(大廳自己輪詢)。`roomSeq` 0 = 人在大廳、>0 = 在那個**門牌**的房(不是加入用的 code)。「誰是我的好友」server 不知道 —— 好友清單存在玩家自己機器上,比對在 client 做 |
| 座位 | `kickUser` / `setSeatClosed` / `transferHost` / `kicked`{reason} / `error`{rq?,code,msg} |
| 組隊 | `assignTeams`{layout:"2v2"/"3v3"/"2v2v2"} / `setOwnTeam`{team:0..3} |
| 開場 | `setReady` / `setSong`{NetSongRef} / `setRoomSettings` / `requestStart`{force,resolved} → `matchStarting`{matchId,startEpochMs,loadTimeoutMs,participants[],spectatorNames[],resolved,song,settings} / `setPlayState` / `gameplayStarted` / `gameplayAborted` / `resultsReady`{matchId,rows[]} |
| 缺歌 | `setAvailability`{packId,state,progress} / `blobQuery`→`blobInfo` / `blobUploadBegin`→`blobUploadAccept`{need[]}→(chunks)→`blobUploadDone` / `blobProgress` / `blobDownloadBegin`→`blobManifest`→(chunks)→`blobDownloadDone` / `blobAvailable` / `blobError` |
| 分數流 | `frame`{matchId,tMs,score,combo,maxCombo,hp,p,c,b,m} C→S / `frames`{matchId,leaderUserId,f:[…]} S→C(**server 攢所有人最新一筆固定 5 Hz 推一次** → N 人下行 N×5 而不是 N²；`leaderUserId` = 權威領隊,見下) / `playFinished` / `comboMilestone`{matchId,combo} C→S→C |
| 房間走動 | `move`{roomCode,roomRev,slot,x,z,f,w} C→S / `moves`{roomCode,roomRev,m:[…]} S→C(同上,但頻率高一點；`slot`=座位 0..5 或旁觀 1000+索引，遲到的舊身分移動會被丟棄) |
| 外觀 | `setLook`{gender,bodyIndex,parts[]} —— 握手時玩家還沒選性別/還沒讀 profile,所以外觀要另外送 |
| 身分 | `setIdentity`{name,playerId,guild,guildEmblem,level} —— 同上的另一半:**選性別 == 選帳號**(女角/男角是兩個 profile,名字不一樣),只送 `setLook` 的話別人看到「新的男角模型 + 舊的女角名字」。兩者都在建房/加入/旁觀**之前**送 |
| 旁觀 | `spectate`{code} / `stopSpectate` |
| 聊天 | `chatSay`{text,channel,expressionId,leading} / `chatMsg` / `announce` —— `channel=="family"` **只轉發給同族**(家族名 + 徽章都一樣,見 `Sdo.Net.GuildIdentity`),房間內與大廳都濾;沒有家族的人送家族頻道只有自己收得到 |
| 密語 | `chatWhisper`{target,text,expressionId,leading,channel} C→S / `whisperMsg`{kind:`out`/`in`/`noid`,party,senderUserId,text,expressionId,leadingText,channel} S→C(見下) |

`playState`:`idle` `ready` **`waitingForLoad`** `loaded` `readyForGameplay` **`playing`** `finished`
**`results`** `spectating`(粗體 = server 保留,client 送不進來)。

### 握手時的身分:`authToken`

server 沒給 `--tokens` 時 `authToken` 被忽略,身分 = client 自稱的 `playerId`/`name`(LAN 模式)。

給了 token 檔之後**server 說了算**:查得到就用 token 綁的 `playerId`/`name` 覆蓋 client 自稱的那組,
查不到 → `bye{badToken}`。所以「把 hello 的 playerId 改成別人的」在啟用 token 之後不再有效。

⚠️ `setIdentity` 是握手**之後**改身分的路徑,所以它必須尊重同一條規則:token 綁了 `name`/`playerId` 的連線
改不動那兩項(只吃得到 `guild`/`guildEmblem`/`level`)—— 否則它就是 token 機制的後門,hello 擋下的冒用改成事後再送一次就成立。

同一條連線上還有另外兩道在 hello **之前**就生效的門(連線在握手之前已經成立):
來源允許名單 → `bye{notAllowed}`、per-IP 連線數上限 → `bye{tooManyFromIp}`。

加密不在協定層:TLS 包在 framing 外面(`[len][kind][payload]` 一個 byte 都沒變),
所以 `serverTls` 只影響 stream 怎麼建起來。憑證釘選規則見 `Sdo.Net.TlsPinning`。

### 名字唯一:`bye{nameTaken}`

**同一個名字同時只能有一個人在線**,擋的是**後上線的那個** → `bye{nameTaken}`,
先在線的完全不受影響(反過來做的話,被冒名等於送對方一把把你踢下線的鑰匙)。

為什麼:名字是這裡唯一認人的東西 —— 密語照名字找人、房間的名字牌、大廳的線上名單都是它。
兩個「小明」同時在線的話密語只進得去其中一個,而寄的人與收的人都不知道為什麼。
比對用 `SanitizeName` **之後**的名字且**不分大小寫**(與密語找人同一條規則,否則
「Alice」與「alice」會同時在線而密語只進得去一個)。

⚠️ `setIdentity` 要尊重同一條規則:改名撞到線上其他人時**不改**(保留原本的名字,其餘欄位照常更新)——
否則用別的名字進來、進來後再改成對方的名字,結果一樣是兩個同名的人同時在線。

代價(不是 bug):client 當掉重開會被自己那條還沒被清掉的舊連線擋住,要等 ping 逾時(15 秒)
把幽靈連線掃掉才進得來。client 端收到這個 code 時彈的是「登入失敗:這個名稱已被使用」
(`net.name_taken`),並**留在選角色畫面**(那裡就有改名字的地方),不像其他連線失敗那樣退回單機。

### `hello.build`:兩邊是不是同一個 commit

client 把視窗標題那串版本(`dance v1.5.0-dev-50359`,build 時由 git 寫進 `productName`)一起送上來,
server 印在連線 log 裡,和自己的版本不同時喊一句警告。server 啟動 banner 也印同格式的
`sdo-server v1.5.0-dev-50359`(見 `Sdo.Server.BuildInfo`)。

🔴 為什麼需要:協定新增訊息型別之後,「忘了更新其中一邊」的症狀是**該功能完全沒反應**
(舊的那一邊不認得,只回一個 `error{proto}`),與「功能本身寫壞了」無法區分。實際踩過兩次,
兩次都花在「到底部署了沒有」上面。版本拿不到的一邊(Unity Editor 的 productName 沒有 git 後綴、
從 tarball 建的 server)則刻意**不**警告 —— 每次連線都喊一句沒意義的話,真的不一致時就沒人看了。

client 收到 `error{proto}` 也會 toast 一句「伺服器版本不符,請更新」。這是 `proto` 與
`rateLimit`/`badJson` 的差別:後兩者不是玩家能處理的事,而版本不符他可以去更新。

### 密語:收件人由 server 找,而且跨房

`chatSay` 的收件人是「房裡所有人」,密語是「全服照名字找出來的那一個人」—— 兩者收件人完全不同,
所以密語是獨立的訊息型別而不是 `chatSay` 的一個欄位。**對方在大廳、在別間房、在旁觀都要收得到**。

三種結果都由 server 回,**連發送者自己那行「你對X說」也是**(`kind=out`):

| kind | 收件人 | 顯示 | `party` |
|---|---|---|---|
| `in` | 目標 | 「X 對你說」 | 發送者的名字 |
| `out` | 發送者 | 「你對 X 說」 | 收件人的名字(**server 認定的正規大小寫**) |
| `noid` | 發送者 | 「找不到玩家 X」 | 玩家原本打的那串字(錯字要照樣顯示,他才知道打錯了什麼) |

🔴 本機送出後**不畫任何東西**,三行都等 server 回來才出現 —— 與公開發言同一套哲學:
「名字到底存不存在」只有 server 知道(它才有全服在線名冊),本機先畫了才發現送不到就是騙人。
這裡實際踩過:client 端曾把密語轉給離線實作,而離線那份比的是寫死的假名冊,
結果線上密語**任何**真人都回「找不到玩家」。

名字比對不分大小寫;同名時送給 userId 最小的那個(先上線的)—— 名字在 server 這邊不保證唯一,
不挑一個穩定規則的話「密語進到誰的視窗」會隨 Dictionary 列舉順序而變。

⚠️ server 只有「現在連著的人」這份名冊,所以無法區分「查無此人」與「這個人存在但沒上線」,兩者都回 `noid`。
單機離線版另外有的「X 不在當前頻道」(`WhisperKind.OffChannel`)因此在連線時不會出現。

密語與公開發言**共用同一個洗頻窗**(chat 5/3s),否則它就成了繞過聊天限速的後門。

### 為什麼推整份 snapshot 而不是 delta

6 人 × 約 1 KB 不是問題,而它消滅一整類「兩邊狀態慢慢漂開」的 bug。`rev` 單調遞增。
**而且不做樂觀更新**:按了按鈕不改本機畫面,等 server 的下一份 snapshot ——
server 會拒絕(不是房主、房間開打了、座位滿了),樂觀更新會顯示一件沒發生的事。

### `frames.leaderUserId` —— 誰站在領隊格

中央前排那一格(鏡頭錨定的位置)是 **server 說了算**,client 收到就照用
(`FormationAssignment.ResolveLeader`)。不讓每台自己算最高分,是因為每台手上的對手分數
新舊不一,同一個人會在別人畫面上站中央、在自己畫面上站旁邊。

而 server 自己也**不能**直接比「最後收到的分數」:每個人的 frame 是 5 Hz、lossy、各自的時鐘,
A 的最新一筆可能是歌曲時間 10000ms 的、B 的是 9600ms 的 —— 直接比就是拿不同時刻的分數比大小,
那 400ms 落差在高 combo 下值好幾千分,leader 就會每 200ms 交替一次。

所以照 osu 的多人排行榜做法(`SpectatorScoreProcessor.UpdateScore` + `MultiplayerLeaderboardProvider.sort`
的節流),三層、沒有分數門檻:

1. **同一時刻取樣** —— 每人的 (`tMs`, `score`) 存成序列,取樣點 = 全場最新歌曲時間 − 500ms,
   各取「不晚於它的最後一筆」(sample-and-hold)。掉包的人就 hold 住上一筆,不會變 0 分。
2. **換人節流** —— leader 最多每 1000ms(歌曲時間)換一次。這是頻率上限,與分數增量大小無關。
3. **決定性 tie-break** —— 同分照 (seat, userId) 排,而且同分不換位。
4. **leader 離場補位** —— 領隊格不能空著,所以補位不受節流限制;但補的是「當下取樣點上分數最高的
   那位」,不是座位序最前的那位。挑座位序會讓玩家看到**兩次**換位(先滑到站錯的人、等滿一輪節流
   才滑到真正的第一名),而中央前排是鏡頭錨點,鏡頭也跟著多跑一趟。

> 舊版是「挑戰者要領先 300 分才換」。門檻式防抖的有效條件是「門檻 > 雜訊振幅」,而這裡的雜訊
> 振幅 = 時間落差 × 得分率,跟著 combo 一起長 —— 門檻永遠追不上,調大又會鎖死真正的超車。
> 細節與取捨(為什麼取樣點用 max−window 而不是 min)寫在 `server/Sdo.Server/Net/LiveLeaderTracker.cs`。

### 右側名單也要「同一時刻」

`frames` 的三層規則(上一節)治的是 server 選領隊,**client 畫右側名單有同一個病**:
遠端那幾列是 5 Hz 推來的、天生落後約一個往返,本機那一列如果照即時分數畫,自己就永遠比別人快一步
(使用者回報「右邊分數列表沒有同步,自己的分數總是比較快」)。

作法與 server 同一條(sample-and-hold,`ScreenGameplay.RosterLocalScore`):本機把自己的分數存成
`(tMs, score)` 短期歷程(只在變動時記一筆、只留最近 5 秒),畫名單時取**最舊的一筆遠端 frame 的
`tMs`**,把自己的分數倒帶到那一刻。取樣點單調遞增 → 名單上的數字不會倒退;有人卡住不再送 frame 時
落後上限 2 秒,不會讓整張名單跟著他凍住。上方那排大分數仍然是即時的 —— 倒帶只影響名單與「第幾名」。

### 曲末的輸贏:等權威名次,平手照座位序

兩件事一起才會對:

1. **平手照座位序**(`Sdo.Ruleset.RankingBoard`,與 server 的 `ResultRowOrder`、`LiveLeaderTracker`
   同一條規則)。舊版是「同分本機先」—— 那是每台各自成立的規則,同分時兩台都判自己第一名。
2. **曲末不當場定輸贏**(`ScreenGameplay.TickFinishPoseDecision`)。歌一結束那一刻,本機手上的對手
   分數是分數流的最後一筆,少了他最後零點幾秒打的音符;拿它定輸贏,接近時兩台都會覺得自己贏。
   改成等 `resultsReady`(權威名次)才放定格動作,最多等 1 秒 —— 等不到時對手的**最終**成績也早就
   從分數流補上了。定格 pose 本來就要在 2.5 秒後才換結算面板,這段等待不影響時程。

> 症狀長這樣:「結算面板寫我第 2 名,人卻在跳勝利動作」。只修其中一件都還會漏掉另一半的情形。

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
| R9 | `setSong` → 保留全員 ready、全員 `avail=unknown`、`rev++`；新歌確認為 `have` 前不能開始 |
| R10a | `setOwnTeam` 需 `playState==idle` 且未按準備(房主的 Ready 恆 false 所以它一直能換隊) |
| R10b | `assignTeams` 驗座位玩家人數符合 layout,否則 `badTeams` |
| R10c | **`requestStart` 在組隊模式下必須湊出 2+2 / 3+3 / 2+2+2,否則擋住**(含 `force`)。參與者集合是「ready 且 `avail==have`」的座位 |
| R11 | `lookerCount` 縮小到低於現有旁觀人數 → 踢掉最新加入的旁觀者 |
| R12 | `open → waitingForLoad`:**參與者在這一刻凍結** = (是房主 或 已準備)且 `avail==have`;非參與者留在房間 |
| R13 | `waitingForLoad → playing`:沒人還在 `waitingForLoad` 且 ≥1 人 `loaded`;`loaded` 集合為空 → 退回 `open` + `gameplayAborted{noParticipants}` |
| R14 | `playing → results → open`:沒人還是 `playing` → 廣播 `resultsReady` → 回 `idle` |
| R15 | **載入 timeout 30 秒**:還在 `waitingForLoad` → 逐出本場;卡在 `loaded` → **強制轉 `playing`** |
| R16 | 遊玩中斷線 → 逐出本場,最後一筆 frame 仍列入結算(`disconnected:true`);host 斷線 → **本場繼續** |
| R17 | `setReady` 需 `avail=="have"`；之後 `avail` 改變會保留 ready 意願，但非 `have` 時不能一般開始 |
| R18 | `status != open` 的房間**一樣加得進去坐一般座位**(`spectate` / `stopSpectate` 也一樣) —— 坐下的人 `playState=idle`,不會被塞進已開跑的那一場(名單開場即凍結,R12),等下一局 |
| R19 | rate limit:control 32/s、`frame` 20/s、chat 5/3s、`setAvailability{downloading}` 1/500ms。超過丟訊息;持續超過 → `bye{rateLimit}` |
| R20 | 上限:maxRooms 200、maxConnections 256、name ≤16、roomName ≤20、blob 總量 20 GB |
| R21 | 旁觀切換的三道門(見 room-matchmaking.md) |
| D15 | `setReady` 在打歌期間只擋**這一場的參與者**;留在房間的人(缺歌/沒準備/中途坐進來的)照按 —— 那是為了下一局 |

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
