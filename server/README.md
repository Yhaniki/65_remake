# sdo-server — 勁舞團重製版連線伺服器

跑在 Linux(也能在 Windows 上跑來測)的 standalone 伺服器。負責房間狀態、訊息轉發、
以及缺歌時的歌曲暫存。

## ⚠️ 安全性:目前只適合 LAN / 信任的朋友

MVP 階段**沒有帳號認證、沒有加密**:

- `playerId` 與名稱完全由 client 自稱 —— 任何人都能冒用別人的身分
- 連線是明文的
- `--password` 只是個進站門檻,不是認證

已經做的濫用防護:每連線的訊息速率限制、連線數與房間數上限、上傳檔案的路徑與大小驗證、
server 端獨立重算歌曲指紋(不信任上傳者)。這些擋的是「壞掉或惡意的 client 把 server 打爆」,
不是「有人冒用身分」。

**不要直接開在公網。** 要開公網需要先做 token 認證與 TLS(計畫裡排成獨立階段)。

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

## systemd

`/etc/systemd/system/sdo-server.service`:

```ini
[Unit]
Description=SDO remake multiplayer server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=sdo
Group=sdo
WorkingDirectory=/opt/sdo-server
ExecStart=/opt/sdo-server/sdo-server --port 27015 --data /var/lib/sdo-server
Restart=on-failure
RestartSec=5

# 收線:server 會攔 SIGTERM 把連線關乾淨(不會留下半開的 socket)
KillSignal=SIGTERM
TimeoutStopSec=10

# 這個程式只需要讀自己的執行檔與寫資料目錄
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/sdo-server

[Install]
WantedBy=multi-user.target
```

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin sdo
sudo mkdir -p /opt/sdo-server /var/lib/sdo-server
sudo chown sdo:sdo /var/lib/sdo-server
# 把 publish 出來的 sdo-server 放到 /opt/sdo-server/ 並 chmod +x
sudo systemctl daemon-reload
sudo systemctl enable --now sdo-server
journalctl -u sdo-server -f
```

## client 端設定

`DATA/PROFILE/config.ini` 的 `[Net]` 區:

```ini
[Net]
serverAddress=192.168.1.10     ← 留空＝純單機(總開關)
serverPort=27015
serverPassword=abab123         ← 預設值,與 server 的 --password 預設值相同
netAutoDownload=1
netMaxDownloadMb=200
```

`serverAddress` 留空時整個連線層都不會被建起來 —— 單機體驗與加連線之前完全一樣。
連不上會提示並自動退回單機,不會卡在開機畫面。

密碼不符會在開機時被 server 擋掉,client 顯示
「密碼不符 —— 請確認 config.ini 的 `[Net] serverPassword` 與伺服器一致」然後退回單機。

## 在同一台機器上開兩份 client 測試

🔴 `config.ini` 是**全域一份**(在 `<DataRoot>/PROFILE/`),所以同機兩份 client 預設會共用
同一個 `activeId` 並互相 `Save()` 覆蓋 —— 兩邊會變成同一個角色,測不出多人。

用 `SDO_DATA_ROOT` 給第二份 client 一份獨立的 DATA:

```powershell
# 第一份:照常跑
.\dance.exe

# 第二份:複製一份 PROFILE(其餘資產可以共用 —— 用 junction 省空間)
$alt = 'H:\sdo_alt_root'
New-Item -ItemType Directory -Force $alt | Out-Null
robocopy H:\65_remake_clean\DATA $alt /E /XD PROFILE /NFL /NDL /NJH /NJS
robocopy H:\65_remake_clean\DATA\PROFILE "$alt\PROFILE" /E /NFL /NDL /NJH /NJS
$env:SDO_DATA_ROOT = $alt
.\dance.exe
```

然後在兩份的 `config.ini` 裡填同一個 `serverAddress`,但選不同的角色(`activeId`)。

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
