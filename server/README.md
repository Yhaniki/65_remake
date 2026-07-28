# sdo-server — 勁舞團重製版連線伺服器

跑在 Linux(也能在 Windows 上跑來測)的 standalone 伺服器。負責房間狀態、訊息轉發、
以及缺歌時的歌曲暫存。

> 📄 要把它部署到 `srcds.yhaniki.com` 的話看 **[DEPLOY.md](DEPLOY.md)** —— 那份是照著那台主機的
> 實際狀況寫的(只能用 port 27017、磁碟只剩 21G、跟 L4D2 撞 port),這份講的是通用設計與參數。

## ⚠️ 安全性:兩種模式,差別在你給了哪些參數

**預設(什麼都不給)= LAN 模式:沒有帳號認證、沒有加密。**

- `playerId` 與名稱完全由 client 自稱 —— 任何人都能冒用別人的身分
- 連線是明文的(密碼、聊天內容,同一個網路上的人看得到)
- `--password` 只是個進站門檻,不是認證

一直都有的濫用防護(不需要任何參數):每連線的訊息速率限制、連線數與房間數上限、
上傳檔案的路徑與大小驗證、server 端獨立重算歌曲指紋(不信任上傳者)。
這些擋的是「壞掉或惡意的 client 把 server 打爆」,不是「有人冒用身分」。

**要開在公網,四個參數都要給**(設定步驟見 [OPERATIONS.md](OPERATIONS.md) §4):

```bash
./sdo-server --tls-cert /etc/sdo/cert.pfx --tls-pass-file /etc/sdo/pfx.pass \
             --tokens /etc/sdo/tokens.txt --max-per-ip 4 --upload-mb-hour 1024
```

少給任何一個都是裸奔,而裸奔**沒有任何徵兆** —— 所以 server 每次開機都會把
「現在受哪些保護」印出來,`⚠️` 開頭的每一行都是一個缺口。看到這兩行就是還沒準備好開公網:

```
[sdo-server] ⚠️  沒有加密(明文 TCP)。要加密請給 --tls-cert <pfx>。
[sdo-server] ⚠️  沒有帳號認證 —— 身分由 client 自稱。要認證請給 --tokens <file>。
```

## 建置

```bash
# 開發 / 跑測試
dotnet test server/

# Linux 單一執行檔(零外部依賴,約 64 MB)
dotnet publish server/Sdo.Server -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false \
  -o server/Sdo.Server/bin/publish-linux

# Windows(本機測試用)
dotnet publish server/Sdo.Server -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false \
  -o server/Sdo.Server/bin/publish-win
```

**不要加 `-p:PublishTrimmed=true`** —— 省下來的體積不值得 trim 帶來的那類「執行期才炸、
而且只在某條路徑上炸」的問題。

### 為什麼 csproj 直接編遊戲的原始碼

`Sdo.Server.csproj` 用 `<Compile Include>` 拉 `65/My project/Assets/Scripts/Sdo.Net/**`
與 `Sdo.Osu/**` —— 也就是**遊戲實際在用的那份**協定與房間規則,不是另寫一份。

兩邊各寫一份的話,分歧會變成整個專案最難查的 bug:封包在 client 看起來對、在 server
看起來也對,但兩邊對同一個 byte 的解讀不一樣。

`LangVersion` 鎖 8.0(與 Unity 產生的 csproj 同版),所以共用檔用了 C# 9+ 語法時
`dotnet build` 會先擋下來,而不是等到開 Unity 才發現。

`Sdo.Osu/AudioDuration.cs` 被排除 —— 它依賴 NLayer(Unity Plugins 裡的 mp3 解碼 DLL),
而 server 不需要知道音檔多長。

## 執行

```bash
./sdo-server                          # 預設 0.0.0.0:27015,資料放 ./data
./sdo-server --port 30000 --data /var/lib/sdo
./sdo-server --password mysecret      # 換掉預設的進站門檻
./sdo-server --password ""            # 不檢查密碼(誰都能進)
./sdo-server --bind 127.0.0.1         # 只聽本機
./sdo-server -v                       # 印出每一筆訊息(除錯用,量大時很吵)
./sdo-server --help
```

`--port 0` 會讓系統挑一個空閒 port(整合測試用的;正式部署請給明確的 port)。

### 進站密碼

**預設值是 `abab123`,而且是啟用狀態**(不是空密碼放行)。client 的 `config.ini`
預設也是同一個值 —— 所以「兩邊都不改」就能直接連上,而密碼機制一開始就是開著的。

這個「兩邊一致」不是靠人記住:兩邊都指向同一個常數
`Sdo.Net.NetLimits.DefaultServerPassword`(`ServerOptions.DefaultPassword` 與
`RoomConfig.DefaultServerPassword`),各有一條測試釘住。理由是密碼漂移的症狀
**完全看不出根因** —— 玩家只會收到「密碼不符」,不會知道是預設值兩邊不一樣了。

要自己開一台給別人連的,請兩邊都改掉;要開沒密碼的,server 給 `--password ""`、
client 的 `serverPassword=` 留空(留空不會被自動補回預設值)。

密碼不符時 server 會 log 一行,但**只寫「空值 / 另一個值」,不印密碼本體** ——
log 常常被貼進 issue 或截圖。

## 部署、測試、參數 → [OPERATIONS.md](OPERATIONS.md)

「怎麼做」的部分全部集中在那一份,這裡不重複(重複就是兩份真相,而漂掉的那份最後會害人):

* 本機怎麼測(自動驗證腳本、同機兩開的正確做法、dev 環境變數表)
* 開給區網的朋友:防火牆、client 的 `config.ini` 要填什麼
* **開在公網:四道防線的完整設定步驟**(TLS 憑證怎麼產、指紋貼哪裡、token 檔格式、systemd unit)
* server 與 client 的**參數全表**(預設值、夾值、什麼時候要改)
* 症狀 → 原因 → 去哪個 log 看
* 日常維運:磁碟怎麼長、要不要備份、怎麼升級

## 架構

```
Sdo.Server/
  Program.cs           參數 → 建目錄 → Hub.Run()
  ServerOptions.cs     命令列解析(純函式,可單測)
  Net/Connection.cs    一條 TCP 連線:framing + 有界 outbound 佇列
  Net/Hub.cs           ★ 單執行緒 actor loop,獨佔房間狀態
  Net/Hub.Handlers.cs  訊息 dispatch 與各 handler
```

房間規則本身**不在這裡** —— 它在 `Sdo.Net/Server/`(`NetRoom` / `RoomRegistry`),
是零 IO、零 socket 的純邏輯。這樣換來三件事:

1. Hub 在單一執行緒上獨佔它 → 完全不需要 lock
2. 每條規則都能直接單元測試(`dotnet test` 秒級,不用開 socket)
3. client 端的 loopback 假伺服器可以驅動**同一份**程式碼 → UI 開發不需要真 server,
   而且假伺服器行為與線上逐位元組相同

### 執行緒歸屬

- **讀**:每條連線自己的 Task。收到完整 frame 後把工作 `Post` 進 Hub 的 actor loop ——
  reader 執行緒**不碰**任何共享狀態。
- **actor loop**:所有房間狀態變更 + 定期工作(載入逾時、分數流彙整、ping 逾時掃描)。
  取工作時帶 50ms timeout,超時就跑一次定期工作 —— 所以連計時器也在同一執行緒上。
- **寫**:每條連線自己的 writer Task,消化一個**有界**佇列。

佇列滿了的處置刻意分兩種:遊玩中的分數流(`frame`)滿了就**丟掉**(它是最新狀態快照,
下一筆就補上);control 訊息滿了就**斷線**(漏一筆 client 的房間狀態會永久偏離,
與其顯示錯的東西不如讓它重連拿一份完整快照)。

## 協定

裸 TCP,frame 格式 `[uint32 LE payloadLen][byte kind][payload]`,kind `0`=UTF-8 JSON、
`1`=binary chunk(檔案傳輸)。每個 JSON 訊息都有 `"t"` 欄位當唯一的 dispatch key。

零多型、零反射 —— 這是刻意避開 osu 的坑:它用 SignalR 的 MessagePack `[Union]` 做多型,
代價是一張手維護的 40+ 筆型別對照表,忘了登記就是執行期爆炸。

訊息清單見 `Sdo.Net/NetProto.cs`;上限與節奏常數見 `Sdo.Net/NetLimits.cs`(每個數字都註明來源)。
