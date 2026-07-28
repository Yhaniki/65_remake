# 網路連線(Networking)

> **這份文件在 2026-07 被整份重寫。** 舊版寫的是「MVP 用 FishNet」的規劃,那條路沒有走 ——
> 實際做出來的是一台自己寫的 **standalone C# server + 裸 TCP**。舊版的表格(FishNet Transport
> 的 Tick/Loss 指標、server-authoritative 判定)描述的東西不存在,所以整份換掉而不是加註。

## 一句話

一台跑在 Linux 的 standalone server 保管房間狀態、轉發訊息、暫存缺歌的檔案;
遊戲用裸 TCP 連上去。**`config.ini` 的 `[Net] serverAddress` 留空就完全是單機**
(走 `MockRoomService`,與加連線之前一字不差)。

## 為什麼不是 FishNet / Mirror / Steam

| 選項 | 為什麼沒用 |
|---|---|
| FishNet / Mirror | 它們解的是「同步一堆 GameObject 的 transform 與 RPC」。這個遊戲要同步的是**房間狀態機**(座位、準備、選歌)與**每人一個分數**;舞蹈是編舞驅動的(同一首歌大家跳一樣),沒有位置需要同步。用一整套 NetworkBehaviour 來搬六個整數,複雜度全是白付的。 |
| WebSocket (`ClientWebSocket`) | client 端有,但 server 端要一個 WS 實作 → 拖進 ASP.NET Core。為了握手協定多背一個框架不值得。 |
| Steam / PlayFab | 這是重製一個已經關服的遊戲,玩家是「自己和朋友幾個人」。綁平台帳號等於把「開一台自己的 server 給朋友連」這件事變不可能。 |
| 裸 TCP ✅ | `ProjectSettings.asset` 的 `apiCompatibilityLevel: 6` = .NET Standard 2.1 + Mono → `System.Net.Sockets` 與 `SHA256` 都在。server 端 net8.0 用同一份原始碼。 |

## 佈線

```
Unity client                                   server (net8.0, Linux)
─────────────────────────────────────          ───────────────────────────
Game/Net/NetConnection.cs   TCP + framing      Net/Listener.cs   TcpListener
Game/Net/NetSongFetcher.cs  第二條 file 連線     Net/Connection.cs 每連線一個讀迴圈
Game/Net/NetClient.cs       房間狀態維護層        Net/Hub.cs        單執行緒 actor loop
UI/Services/OnlineRoomService.cs  接既有介面      Net/Hub.Handlers.cs 每個訊息一個 handler
                                               Net/Hub.Blobs.cs  缺歌傳檔
                                               Files/            blob 倉庫 + 定期清理
      ↕ 共用原始碼(client 與 server 編**同一份檔案**)
Sdo.Net/     協定 DTO + 房間規則(noEngineReferences)
Sdo.Osu/     歌曲指紋 SongPackId / 過濾 SongPackFilter / 路徑安全 SafeRelPath
```

🔴 **server 直接編譯遊戲實際在用的那份共用原始碼**(`Sdo.Server.csproj` 的
`<Compile Include="../../65/My project/Assets/Scripts/Sdo.Net/**/*.cs" />`)。
兩邊各寫一份 parser 或一份房間規則,分歧會變成整個專案最難查的那種 bug ——
封包在 client 看起來對、在 server 看起來也對,但兩邊對同一個 byte 的解讀不一樣。

## 執行緒模型

- **client**:socket 讀寫各一條背景 thread,收到的 frame 進 `ConcurrentQueue`;
  主執行緒每幀 `Pump()` 消化。所有房間狀態只在主執行緒被碰到。
  ⚠️ `OnDisable` / `AssemblyReloadEvents.beforeAssemblyReload` 一定要 `Close()` + join(帶 timeout),
  否則 editor 的 domain reload 會卡死。
- **server**:每連線一個 async 讀取 Task,但**所有房間變更都 marshal 到 `Hub` 的單執行緒 actor loop**。
  所以 `RoomRegistry` / `NetRoom` 零 lock、可以直接單元測試,也能被 client 端的 loopback 假伺服器重用。
  跨房操作(「已在別房 → 先隱式離房」)在細粒度鎖下是死鎖溫床,單執行緒直接免疫。
  actor loop 順便當計時器(50ms 一輪):載入逾時、分數流彙整、ping 逾時、歌曲暫存清理。

## 狀態同步的原則

**每次變更推整份 `roomState` snapshot,不推 delta。** 6 人 × 約 1 KB 不是問題,而它消滅一整類
「兩邊狀態慢慢漂開」的 bug。`rev` 單調遞增,client 只要比 rev 就知道要不要重畫。

**不做樂觀更新。** 按了按鈕不會先改本機畫面 —— 等 server 的下一份 snapshot。
理由很實際:server 會拒絕(不是房主、房間開打了、座位滿了),樂觀更新會讓畫面顯示一件沒發生的事。

## 診斷(這一段是實際會用到的)

連線功能「按了沒反應」時,先做這兩件事,不要猜:

1. **`SDO_VERBOSE=1`** —— 打包版預設**完全不把 `Debug.Log` 寫進 log.txt**(`SdoLog.OnLog`)。
   沒開這個開關,整個專案的 info 級診斷在打包版都是啞的。
2. **server 的 stdout 導進檔案**。`SendError` 會記下「哪一條規則拒絕了誰」——
   那是唯一的痕跡。(`tools/shoot_room_bubble.ps1` 用 `-RedirectStandardOutput server_run.log`。)

`NetClient.Diagnostics` 會吐連線狀態 / userId / rev / RTT,debug overlay 讀它。

多開測試的 dev hook(注入滑鼠點按鈕需要精確的設計→螢幕座標換算,而那條換算有已知偏移,
所以走「與玩家按下去同一條路」的 hook 比較可靠):

| 變數 | 作用 |
|---|---|
| `SDO_ROOM=1` | 開機直接開房(房主) |
| `SDO_JOINFIRST=1` | 開機直接加入第一間房 |
| `SDO_SAY=<文字>` | 自動說一句話(會讓聊天框 armed → 把 F2 捷徑擋掉) |
| `SDO_AUTOREADY=1` | 非房主自動按準備 |
| `SDO_AUTOSTART=1` | 房主自動開始(**會等第二個人坐下** —— 不等的話房主在自己開機那幾秒就 solo 開場) |
| `SDO_AUTOPLAY=1` | 代打(驗分數流一定要用它:亂按 lane 鍵幾乎全是 MISS,負分夾到 0,兩台都停在 0 什麼都證明不了) |
| `SDO_PICKSONG=<歌名片段>` | 房主自動選一首外部歌(缺歌傳檔的實機驗證) |
| `SDO_DANCERS=<n>` / `SDO_ROOMAVATARS=<n>` | 效能量測:場上 / 房間補到 n 隻角色 |
| `SDO_DATA_ROOT=<路徑>` | 換一份 DATA(同機兩開必須,否則共用 `config.ini` 的 `activeId` 會互相覆蓋) |

## 安全性(現況與界線)

**預設模式(server 沒給公網參數)= 沒有帳號認證、沒有加密,`playerId` 完全由 client 自稱。**
那是 LAN / 信任的朋友之間的模式,也是預設值。

**開公網要給四個參數**(`--tokens` / `--tls-cert` / `--max-per-ip` / `--upload-mb-hour`);
完整步驟見 [server/README.md](../../server/README.md) 的公網化一節。兩件跟 client 有關的:

- `config.ini` 的 `serverTls=1` + `serverCertFingerprint=<server 開機印出來那串>`。
  自簽憑證**一定要填指紋** —— 自簽沒有 CA 背書,一般驗證必定失敗,而「驗證失敗就放行」
  會讓 TLS 只剩裝飾(中間人插一台假 server,加密照樣成立,只是加密給攻擊者)。
  填了指紋 = 只認那一張憑證;兩者都不成立時 client 連不上,**不會默默退回明文**。
  比對規則是純函式 `Sdo.Net.TlsPinning`(client/server 共用同一份,有測試守「空指紋不符合任何東西」)。
- `serverToken=` 對應 server 的 token 檔。啟用後身分由 server 決定,client 自稱的 `playerId` 被忽略。
  token 是共享機密 → **TLS 是搭配條件不是選項**(明文連線上的 token 等於公開的)。

而**濫用防護一直都在**(不需要任何參數)—— 事後補會動到協定:
- rate limit(control 32/s、frame 20/s、chat 5/3s),持續超過就斷線
- 每個 host-only 操作 server 獨立驗一次(client 只是隱藏按鈕,兩層都要做)
- server 保留的狀態(`waitingForLoad`/`playing`/`results`)client 送不進來
- 傳檔:路徑過 `SafeRelPath`、單檔/總量上限、**重算 packId、重算每個檔的 SHA-256**
- frame payload 上限在 **allocate 之前**檢查(否則壞掉的 length 直接 OOM)

進站密碼(`config.ini` 的 `[Net] serverPassword`,預設 `abab123`)只是一個門檻,不是認證。
密碼不符時 server 記 log 但**不印密碼本身**(log 常被貼到 issue 或截圖)。

## 相關

- [net-protocol.md](net-protocol.md) —— 每一個訊息、每一條房間規則
- [net-song-transfer.md](net-song-transfer.md) —— 缺歌自動傳檔
- [room-matchmaking.md](room-matchmaking.md) —— 房間/座位/開場流程
- [../architecture/online-services.md](../architecture/online-services.md) —— 為什麼自己寫 server
- [debug-overlay.md](debug-overlay.md)
