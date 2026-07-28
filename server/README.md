# sdo-server — 勁舞團重製版連線伺服器

跑在 Linux(也能在 Windows 上跑來測)的 standalone 伺服器。負責房間狀態、訊息轉發、
以及缺歌時的歌曲暫存。

## ⚠️ 安全性:兩種模式,差別在你給了哪些參數

**預設(什麼都不給)= LAN 模式:沒有帳號認證、沒有加密。**

- `playerId` 與名稱完全由 client 自稱 —— 任何人都能冒用別人的身分
- 連線是明文的(密碼、聊天內容,同一個網路上的人看得到)
- `--password` 只是個進站門檻,不是認證

一直都有的濫用防護(不需要任何參數):每連線的訊息速率限制、連線數與房間數上限、
上傳檔案的路徑與大小驗證、server 端獨立重算歌曲指紋(不信任上傳者)。
這些擋的是「壞掉或惡意的 client 把 server 打爆」,不是「有人冒用身分」。

**要開在公網,四個參數都要給**(見 [公網化](#公網化) 一節):

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

## 公網化

四道防線,**全部都是「給了參數才生效」** —— 不給就完全是 LAN 模式的行為(所以在家裡玩的人
不必為了公網功能付任何代價)。

| 參數 | 擋什麼 | 不給的後果 |
|---|---|---|
| `--tls-cert <pfx>` | 竊聽與中間人 | 密碼/token/聊天全明文 |
| `--tokens <file>` | 冒用身分 | 誰都能自稱是你 |
| `--max-per-ip <n>` | 一個人開一百條連線佔滿 server | 一台機器就能讓別人連不進來 |
| `--upload-mb-hour <n>` | 拿 server 當免費網路硬碟 | 磁碟被塞滿(TTL 清理來不及) |

### 1. TLS 加密

```bash
# 自簽憑證(自己跟朋友玩,最常見的情況)。-days 3650 = 十年,免得忘記換。
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
  -keyout key.pem -out cert.pem -subj "/CN=dance.example.com" \
  -addext "subjectAltName=DNS:dance.example.com"

# 合成 server 要的 pfx(密碼可以留空;有密碼就用 --tls-pass-file 帶進去)
openssl pkcs12 -export -out cert.pfx -inkey key.pem -in cert.pem -passout pass:

./sdo-server --tls-cert /etc/sdo/cert.pfx
```

開機時 server 會印出憑證指紋:

```
[sdo-server] TLS 已啟用(TLS 1.2/1.3)。憑證指紋 SHA-256:
[sdo-server]   6473b40678340324aa73a7cd6144d2168cdbc24f3d3a04ef469a672cc2c92e22
```

**把那一串貼進每個玩家的 `config.ini`**:

```ini
serverTls=1
serverCertFingerprint=6473b40678340324aa73a7cd6144d2168cdbc24f3d3a04ef469a672cc2c92e22
```

為什麼要貼指紋:自簽憑證沒有 CA 背書,一般的驗證流程必定失敗。於是最容易犯的錯就是在 client 的
驗證 callback 裡直接放行 —— **那樣 TLS 只剩裝飾**:任何人都能在中間插一台假 server,
加密照樣成立,只是加密給攻擊者。填了指紋之後 client 只認「指紋一模一樣」的那張憑證,
鏈結錯誤才可以忽略。指紋留空 = 走一般 CA 驗證(用 Let's Encrypt 之類的正式憑證時適用);
兩者都不成立時 client **連不上**,不會默默放行。

其他行為:

- 憑證讀不到 / 沒有私鑰 / pfx 密碼錯 → **server 直接開不起來**(exit 4)。
  退回明文是最糟的選擇:使用者以為是加密的,而且完全沒有徵兆。
- pfx 密碼請用 `--tls-pass-file`。`--tls-pass` 會出現在 `ps` / `/proc` 裡,同一台機器上誰都看得到。
- 只開 TLS 1.2 / 1.3。client 端談的是 1.2(Unity 的 Mono 對 1.3 的支援視版本而定)。
- 握手有 10 秒逾時,而且**跑在各自的連線 task 上** —— 連上來不講話的人不會擋住別人進來
  (有一條回歸測試守這件事:先弄壞一次握手,再確認正常 client 還連得進來)。
- 傳檔的第二條連線走同一個 port,所以加密設定當然一樣,client 兩條都讀同一組 config。

### 2. token 認證

一行一個 token 的純文字檔;`#` 開頭是註解。`=` 後面可以接「這個 token 是誰」
(`playerId, 顯示名稱, admin`,都可以省略):

```
# /etc/sdo/tokens.txt —— chmod 600,只有跑 server 的那個帳號讀得到
9f2c1ab34de5f6079f2c1ab34de5f607
c0ffee1234567890c0ffee1234567890 = 00000001
deadbeefdeadbeefdeadbeefdeadbeef = 00000002, 小明
0123456789abcdef0123456789abcdef = 00000003, 管理員, admin
```

只給 token(第一行)= 認證通過,但身分沿用 client 自稱的;要**真的**阻止冒用就要綁 playerId。

產生 token:`openssl rand -hex 16`(32 個字,遠超過下限)。

```bash
./sdo-server --tokens /etc/sdo/tokens.txt
```

啟用之後**身分由 server 決定**:client 送上來的 `playerId` 被忽略,改用 token 對到的那一個。
沒帶 token 或帶錯 → `bye{badToken}`,client 顯示「伺服器不認得這個 token」。
token 太短(< 16 字元)會在開機時被拒絕並在 log 說明 —— 短 token 是可以猜的。

玩家端填 `config.ini` 的 `serverToken=`。

> ⚠️ token 是**共享機密**,只要拿到就是那個身分。所以 TLS 是搭配條件,不是選項:
> 明文連線的 token 在網路上等於公開的。

### 3. 連線來源與 per-IP 上限

```bash
./sdo-server --allow-from "192.168.0.,203.0.113.7"   # . 結尾 = 前綴網段
./sdo-server --max-per-ip 4                          # 一份 client 正常用 2 條(control + file)
```

兩者都在**握手之前**就擋掉(連線在 hello 之前已經成立 —— 「開一百條連線把 `--max-conns`
佔滿」不需要通過任何認證就做得到)。

### 4. 上傳配額

```bash
./sdo-server --upload-mb-hour 1024      # 每人每小時 1 GB;0 = 不限
```

超過就回 `blobError{quota}`,房主那邊看得到原因。歌曲暫存本來就有 TTL(預設 24 小時)與
總容量上限(`--max-blob-gb`),配額擋的是「在 TTL 到之前就把磁碟塞滿」。

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
# 開在公網的話換成這行(見「公網化」一節;憑證與 token 檔放在 /etc/sdo,chmod 600 給 sdo 讀):
# ExecStart=/opt/sdo-server/sdo-server --port 27015 --data /var/lib/sdo-server \
#   --tls-cert /etc/sdo/cert.pfx --tls-pass-file /etc/sdo/pfx.pass \
#   --tokens /etc/sdo/tokens.txt --max-per-ip 4 --upload-mb-hour 1024
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
serverToken=                   ← 公網 server 才要填(server 有 --tokens 時)
serverTls=0                    ← 1 = 加密。server 有 --tls-cert 時必須設 1
serverCertFingerprint=         ← 自簽憑證必填(server 開機會印出來);正式憑證留空
netAutoDownload=1
netMaxDownloadMb=200
```

`serverAddress` 留空時整個連線層都不會被建起來 —— 單機體驗與加連線之前完全一樣。
連不上會提示並自動退回單機,不會卡在開機畫面。

密碼不符會在開機時被 server 擋掉,client 顯示
「密碼不符 —— 請確認 config.ini 的 `[Net] serverPassword` 與伺服器一致」然後退回單機。
token 不對是同一條路徑(顯示「伺服器不認得這個 token」)。

TLS 設定不一致時的症狀:

| 情況 | 看到什麼 |
|---|---|
| server 開了 TLS,client `serverTls=0` | 連不上(server log 有一行 TLS 握手失敗);**絕不會退回明文** |
| server 沒開 TLS,client `serverTls=1` | 握手失敗,client 顯示 TLS 錯誤 |
| 自簽憑證但沒填指紋 | 「憑證驗證失敗… 自簽憑證要在 config.ini 填 serverCertFingerprint」 |
| 指紋填錯 | 「憑證指紋不符(設定的是 xxx…,收到的是 yyy…)」—— 訊息帶兩邊前 16 碼,好對照 |

`serverCertFingerprint` 會被正規化成「64 個小寫 hex」:冒號、空白、大小寫都隨你貼,
但格式不對就當沒填 —— 那會讓連線在握手時明確失敗,而不是靜默放行一張不對的憑證。

## 在同一台機器上開兩份 client 測試

🔴 `config.ini` 是**全域一份**(在 `<DataRoot>/PROFILE/`),所以同機兩份 client 預設會共用
同一個 `activeId` 並互相 `Save()` 覆蓋 —— 兩邊會變成同一個角色,測不出多人。

用 `SDO_DATA_ROOT` 給第二份 client 一份獨立的 DATA。`tools\make_alt_data_root.ps1` 會建好:

```powershell
# 一次就好:建一個 link farm(除 PROFILE 以外全部 junction 回原本那棵樹,幾 GB 不用複製),
# 並把第二份的 activeId 改成 00000001(男)—— 兩邊看起來不一樣,一眼分得出誰是誰。
.\tools\make_alt_data_root.ps1

# 第一份:照常跑
.\Build\Windows\dance.exe

# 第二份:換 DATA root
$env:SDO_DATA_ROOT = 'H:\sdo_alt_root'
.\Build\Windows\dance.exe
```

兩份的 `config.ini` 要填同一個 `serverAddress` 與同一個 `serverPassword`。

房號是 server 隨機配的,手動把它抄到第二份很煩 → 有兩個 dev 環境變數:

```powershell
$env:SDO_ROOM = 1        # 開機直接開一間房(第一份)
$env:SDO_JOINFIRST = 1   # 開機直接加入 server 上第一間房(第二份)
$env:SDO_JOINDLG = 1     # 開機直接彈「輸入房號」框(只是要看那個框的排版)
```

### 截圖驗證

UI 改動一律實機截圖驗證(烘圖工具的輸出看起來對,疊上 TMP 之後字有沒有對準是另一回事)。
`tools\shoot_ui.ps1` 把「啟動 → 等開完機 → 抓視窗 → 關掉」自動化:

```powershell
.\tools\shoot_ui.ps1 -Out shot_gendersel.png
.\tools\shoot_ui.ps1 -Env SDO_JOINDLG=1 -Out shot_joindlg.png
.\tools\shoot_ui.ps1 -Env SDO_ROOM=1 -Out shot_room.png -KeepOpen
```

⚠️ 連線相關的畫面(選男女畫面的三顆鈕、房號框)**只有真的連上 server 才會出現** ——
連不上會退回單機版面(兩顆鈕)。截圖前先確認 server 有在跑,而且
`<DataRoot>\PROFILE\config.ini` 的 `[Net] serverAddress` 有填。
DataRoot 看 repo 根的 `data_root.txt`,**不是** exe 旁邊那份 DATA(`log.txt` 第一行會印出實際用的那個)。

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
