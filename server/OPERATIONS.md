# sdo-server 運維手冊 —— 測試、部署、參數

這份是**照著做**的手冊:要怎麼測、怎麼開起來、每個參數是什麼意思、出事了去哪裡看。
設計理由(為什麼是裸 TCP、為什麼歌曲用內容尋址、為什麼 server 不做權威判定…)在
[README.md](README.md) 與 [docs/systems/networking.md](../docs/systems/networking.md)。

> 📄 **要部署到 `srcds.yhaniki.com` 那台就直接看 [DEPLOY.md](DEPLOY.md)** —— 那份是照那台主機的
> 實際狀況寫的(port 8888、磁碟只剩 21G、用 tmux + 獨立的 `sdo` 帳號跑),照著抄就好。
> 這份是通用的:換一台機器、或想知道某個參數到底在做什麼的時候看這份。

所有指令都在這個 repo 的根目錄執行。Windows 的指令用 PowerShell,Linux 的用 bash。

---

## 0. 先決定你要哪一種

| 情境 | 要做什麼 | 安全性 |
|---|---|---|
| **A. 自己一台機器測** | 跑 `dotnet build` 出來的 exe + 驗證腳本 | 只聽 127.0.0.1,不用管 |
| **B. 開給同一個區網的朋友** | publish → 跑起來 → 開防火牆 → 三個人改 `config.ini` | 密碼是唯一的門檻,身分自稱、明文 |
| **C. 開在公網** | B 的全部 + **四道防線一個都不能少** | 見 §4;少給一個就是裸奔 |

🔴 **B 與 C 的差別不是「網路設定」,是四個參數。** server 開機會把現在受哪些保護印出來,
`⚠️` 開頭的每一行就是一個缺口 —— 那是唯一可靠的自我檢查(這四道防線沒生效時**沒有任何徵兆**)。

---

## 1. 建置

### server

```bash
# Linux 單一執行檔(零外部依賴,實測 64 MB)
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

本機測不必 publish,`dotnet build server/` 之後直接用
`server/Sdo.Server/bin/Debug/net8.0/sdo-server.exe` 就好。

**server 不需要遊戲資產。** 它從來不讀 `DATA/`(歌曲是玩家上傳進 blob 暫存的),
所以部署只有兩樣東西:一個執行檔 + 一個可寫的資料目錄。

### client

```powershell
.\tools\build_windows.ps1        # → Build\Windows\dance.exe(順便打包 DATA)
```

⚠️ 這支腳本**會回非零 exit code**,但最後幾行看起來仍像正常結束。要確認成功請看
`=== Unity exit code: N ===` 那一行(0 才是成功),不要只看 tail。
另外 `dance.exe` 本身的時間戳不會變(Unity 內容沒變就不重寫那個 launcher)——
要確認 build 有沒有更新,看 `Build\Windows\dance_Data\Managed\Sdo.UI.dll` 的時間。

---

## 2. 情境 A:在自己這台機器測

### 最快:自動驗證腳本

```powershell
# 一台 server + 兩台 dance.exe(各自的 DATA root),跑完印一張檢查表
.\tools\verify_online.ps1                      # 明文、不驗 token
.\tools\verify_online.ps1 -Tls -Token          # 順便驗 TLS 與 token 認證
.\tools\verify_online.ps1 -FullRoom -Seconds 120   # 驗「座位滿了自動改用旁觀身分進房」
```

它會自己開 server、自己產自簽憑證、自己改兩份 `config.ini`、開兩台 client、跑完關掉,
再把證據列成 `online_verify.md`。每一條檢查都是「log 裡看得到 / 看不到」,不是「大概有吧」。

前置:`Build\Windows\dance.exe` 與 `server\...\sdo-server.exe` 都存在(見 §1)。
兩份 DATA root 會自動建(`H:\sdo_verify_a` / `H:\sdo_verify_b`,link farm,不是複製)。
🔴 它**不會碰你真正的 `DATA\PROFILE\config.ini`** —— 兩台 client 都用獨立的 root。

| 參數 | 意思 |
|---|---|
| `-Tls` | 自簽一張憑證、從 server 的開機輸出抓指紋、填進兩台 client |
| `-Token` | 產兩個 token 綁兩個 playerId,server 帶 `--tokens` |
| `-FullRoom` | 房主用 `SDO_CLOSESEATS` 關光其他座位做出滿房 → 驗第二台會自動變旁觀 |
| `-Seconds <n>` | 最多等多久(一般模式要等整首歌打完,240 起跳;`-FullRoom` 120 就夠) |
| `-Song <片段>` | 房主要選的外部歌標題片段(預設 `Bassdrop`) |
| `-Port <n>` | server 監聽的 port(預設 27099,避開正式的 27015) |

### 手動兩開

🔴 `config.ini` 是**全域一份**(`<DataRoot>\PROFILE\config.ini`),同機兩份 client 預設會共用
同一個 `activeId` 並互相 `Save()` 覆蓋 —— 兩邊會變成同一個角色,測不出多人。

```powershell
# 一次就好:建第二份 DATA root(除 PROFILE 以外全部 junction 回原本那棵樹,幾 GB 不用複製)
.\tools\make_alt_data_root.ps1                 # → H:\sdo_alt_root,activeId=00000001(男)

# server
.\server\Sdo.Server\bin\Debug\net8.0\sdo-server.exe --bind 127.0.0.1 --data .\_srvdata -v

# 第一份 client(開房)
$env:SDO_VERBOSE = '1'; $env:SDO_LOG = 'H:\a.log'; $env:SDO_ROOM = '1'
.\Build\Windows\dance.exe

# 第二份 client(加入)—— 另開一個 PowerShell 視窗
$env:SDO_VERBOSE = '1'; $env:SDO_LOG = 'H:\b.log'
$env:SDO_DATA_ROOT = 'H:\sdo_alt_root'; $env:SDO_JOINFIRST = '1'
.\Build\Windows\dance.exe
```

🔴 **兩台一定要各給一個 `SDO_LOG`。** 兩份 dance.exe 預設都寫 `<exe 目錄>\log.txt`,
後開的那台在啟動時就把前一台的搬成 `.prev`(等於清掉),之後還會交錯寫 ——
症狀是「log 裡只有一台的訊息而且開頭被截掉」,看起來像功能沒跑。

兩份的 `config.ini` 要填同一個 `serverAddress` / `serverPort` / `serverPassword`。

---

## 3. 情境 B:開給區網的朋友

```bash
# 1) server(換掉預設密碼)
./sdo-server --port 27015 --data /var/lib/sdo-server --password 你們的密碼

# 2) 防火牆:只要開這一個 TCP port
#    控制連線與傳檔連線走**同一個 port**(靠 hello.role 分辨),不必多開
sudo ufw allow 27015/tcp
```

每個玩家改自己的 `DATA\PROFILE\config.ini`:

```ini
[Net]
serverAddress=192.168.1.10
serverPort=27015
serverPassword=你們的密碼
```

驗收:兩台開進同一間房、看得到彼此的角色與名字、選一首歌兩邊一起開場。

---

## 4. 情境 C:開在公網

四道防線,**全部都是「給了參數才生效」**。少給一個就是裸奔,而且沒有任何徵兆。

```bash
./sdo-server --port 27015 --data /var/lib/sdo-server \
  --tls-cert /etc/sdo/cert.pfx --tls-pass-file /etc/sdo/pfx.pass \
  --tokens /etc/sdo/tokens.txt \
  --max-per-ip 4 --upload-mb-hour 1024
```

### 4.1 TLS 加密

```bash
# 自簽(自己跟朋友玩,最常見)。-days 3650 = 十年,免得忘記換
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
  -keyout key.pem -out cert.pem -subj "/CN=dance.example.com" \
  -addext "subjectAltName=DNS:dance.example.com"

# 合成 server 要的 pfx(密碼留空;有密碼就用 --tls-pass-file 帶進去)
openssl pkcs12 -export -out cert.pfx -inkey key.pem -in cert.pem -passout pass:
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

🔴 **自簽憑證一定要貼指紋。** 自簽沒有 CA 背書,一般驗證必定失敗 → 最容易犯的錯就是在 client
的驗證流程裡放行,**那樣 TLS 只剩裝飾**:任何人都能在中間插一台假 server,加密照樣成立,
只是加密給攻擊者。填了指紋之後 client 只認「指紋一模一樣」的那張憑證。
指紋留空 = 走一般 CA 驗證(用 Let's Encrypt 之類的正式憑證時適用);兩者都不成立時 client 連不上。

其他行為:

- 憑證讀不到 / 沒有私鑰 / pfx 密碼錯 → **server 直接開不起來**(退回明文是最糟的選擇:
  使用者以為是加密的,而且完全沒有徵兆)。
- pfx 密碼請用 `--tls-pass-file`;`--tls-pass` 會出現在 `ps` / `/proc` 裡,同機誰都看得到。
- 只開 TLS 1.2 / 1.3。握手有 10 秒逾時,而且跑在各自的連線 task 上 —— 連上來不講話的人
  不會擋住別人進來。

### 4.2 token 認證

一行一個;`#` 是註解;`=` 後面可接「這個 token 是誰」(`playerId, 顯示名稱, admin`,都可省略):

```
# /etc/sdo/tokens.txt —— chmod 600,只有跑 server 的帳號讀得到
9f2c1ab34de5f6079f2c1ab34de5f607
c0ffee1234567890c0ffee1234567890 = 00000001
deadbeefdeadbeefdeadbeefdeadbeef = 00000002, 小明
0123456789abcdef0123456789abcdef = 00000003, 管理員, admin
```

產 token:`openssl rand -hex 16`。**少於 16 個字的 token 會被忽略**(那種長度猜得出來),
開機時會為每一行被忽略的記一句 log(**只寫第幾行,不印 token 本體**)。

啟用之後**身分由 server 決定**:client 送來的 `playerId` 被 token 綁的那個覆蓋。
只給 token 不綁 playerId(第一行那種)= 認證通過但身分仍沿用 client 自稱的 ——
要真的阻止冒用就要綁。玩家端填 `config.ini` 的 `serverToken=`。

> ⚠️ token 是**共享機密**,拿到就是那個身分 → TLS 是搭配條件不是選項(明文連線上的 token 等於公開)。

### 4.3 來源限制與 per-IP 上限

```bash
--allow-from "192.168.0.,203.0.113.7"   # . 結尾 = 前綴網段;空 = 不限
--max-per-ip 4                          # 一份 client 正常用 2 條(control + file)
```

兩者都在**握手之前**就擋掉 —— 連線在 hello 之前就已經成立,「開一百條連線把 `--max-conns`
佔滿」不需要通過任何認證就做得到。

### 4.4 上傳配額

```bash
--upload-mb-hour 1024      # 每人每小時 1 GB;0(預設)= 不限
```

超過回 `blobError{quota}`,房主看得到原因。歌曲暫存本來就有 TTL 與總容量上限,
配額擋的是「在 TTL 到之前就把磁碟塞滿」。

### 4.5 systemd

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
ExecStart=/opt/sdo-server/sdo-server --port 27015 --data /var/lib/sdo-server \
  --tls-cert /etc/sdo/cert.pfx --tls-pass-file /etc/sdo/pfx.pass \
  --tokens /etc/sdo/tokens.txt --max-per-ip 4 --upload-mb-hour 1024
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
sudo mkdir -p /opt/sdo-server /var/lib/sdo-server /etc/sdo
sudo chown sdo:sdo /var/lib/sdo-server
sudo chown root:sdo /etc/sdo/cert.pfx /etc/sdo/pfx.pass /etc/sdo/tokens.txt
sudo chmod 640 /etc/sdo/cert.pfx /etc/sdo/pfx.pass /etc/sdo/tokens.txt
# 把 publish 出來的 sdo-server 放到 /opt/sdo-server/ 並 chmod +x
sudo systemctl daemon-reload
sudo systemctl enable --now sdo-server
journalctl -u sdo-server -f
```

🔴 `ProtectHome=true` 會把 `/home` 遮成空目錄。資料目錄或憑證放在某個人的家目錄底下時,
症狀是「憑證讀不到 → exit 4」而不是權限錯誤。要嘛照上面放 `/var/lib` + `/etc`,要嘛關掉這行。

### 4.5b 不用 systemd:tmux

想要能 `attach` 進去看畫面、或不想動 `/etc/systemd` 的話,[`deploy/sdoctl.sh`](deploy/sdoctl.sh)
把 tmux 那套包成 `start` / `stop` / `restart` / `attach` / `status` / `log`
(設定放 `/etc/sdo/sdoctl.conf`)。代價是**沒有 `Restart=on-failure`、沒有 journald 輪替、
沒有開機自動啟動**(要自己加 `@reboot` crontab)——
取捨與實際用法見 [DEPLOY.md §14](DEPLOY.md#14-為什麼是-tmux-不是-systemd)。

### 4.6 上線前的驗收清單

```
journalctl -u sdo-server -n 20
```

- [ ] 有「TLS 已啟用」與一串 64 字的指紋
- [ ] 有「token 認證已啟用:N 個 token」
- [ ] **一行 `⚠️` 都沒有**
- [ ] 玩家連上之後,連線那行寫的是 `(TLS,共 N 條)` 而不是 `(明文,共 N 條)`

---

## 5. 參數全表

### server(命令列)

| 旗標 | 預設 | 作用 / 夾值 |
|---|---|---|
| `--port <n>` | `27015` | 監聽 port。`0` = 讓系統挑一個空閒的(測試用)。不在 0..65535 → 拒絕啟動 |
| `--bind <addr>` | `0.0.0.0` | 綁定位址。`127.0.0.1` = 只聽本機 |
| `--data <dir>` | `./data` | 資料目錄。🔴 **相對的是「工作目錄」不是執行檔位置** —— systemd 記得給絕對路徑 |
| `--password <pw>` | `abab123` | 進站密碼,**預設是啟用的**(不是空密碼放行)。`""` = 不檢查 |
| `--max-rooms <n>` | `200` | 同時開房上限(最小 1) |
| `--max-conns <n>` | `256` | 連線數上限(最小 2)。一份 client 用 2 條 |
| `--ttl-hours <n>` | `24` | 歌曲暫存保留時數(最小 1) |
| `--max-blob-gb <n>` | `20` | 歌曲暫存總容量上限 GB(最小 1) |
| `--code-seed <n>` | 隨機 | 房號洗牌種子。給固定值 = 房號順序可重現(測試用) |
| `-v`, `--verbose` | 關 | 印出每一筆**收到**的訊息(送出的不印)。量很大,只在查問題時開 |
| `--tokens <file>` | 不啟用 | token 檔。啟用後身分由 server 決定 |
| `--allow-from <list>` | 不限 | 只接受這些來源(逗號分隔;`.` 結尾 = 前綴網段) |
| `--max-per-ip <n>` | `8` | 同一個 IP 的連線數上限。`0` = 不限 |
| `--upload-mb-hour <n>` | `0`(不限) | 每人每小時上傳上限 MB |
| `--tls-cert <file>` | 不啟用 | TLS 憑證(`.pfx`,含私鑰)。給了就加密 |
| `--tls-pass <pw>` | 空 | pfx 密碼。⚠️ 命令列在本機是公開的 |
| `--tls-pass-file <f>` | 空 | 從檔案讀 pfx 密碼(只讀第一行) |

離開碼:`0` 正常結束(含 `--help`)/ `1` 執行期致命錯誤(**port 被佔用是這個**)/
`2` 參數錯誤(含憑證檔找不到)/ `3` 建不出資料目錄 / `4` TLS 設定有問題(拒絕以明文啟動)。

### 🔴 這些不會報錯,但會安靜地害你

逐項回原始碼核對出來的。共同點是**都不會讓 server 開不起來** ——
所以只能靠開機那幾行 log 自己確認:

| 誤設 | 實際會發生什麼 |
|---|---|
| `--bind` 打錯字(`127.0.0.` 或主機名) | 解析失敗 → **靜默退回聽全部介面**。本來只想聽本機,結果對外開放。開機 log 印的是解析後的位址,不是你打的字串 —— 對一下 |
| `--tokens` 路徑打錯 / 讀不到 | **不會拒絕啟動**,只 log 一行,認證維持**關閉**(與 `--tls-cert` 找不到就拒絕啟動剛好相反)。橫幅會出現「⚠️ 沒有帳號認證」 |
| token 檔裡的 token 全都短於 16 字 | 全部被忽略 → 有效 token 數 0 → 等於沒啟用認證(**不是**「誰都進不來」) |
| `--allow-from 192.168.0.0/24` | **不支援 CIDR**,整串被當字面 IP 比對 → 永遠不相等 → 把自己鎖在門外。網段要寫成結尾帶點的前綴:`192.168.0.` |
| `--allow-from 10.1`(結尾沒點) | 純字串前綴比對 → 連 `10.10.x.x` 也放進來了 |
| `--max-per-ip 1` | 一份 client 正常佔 **2 條**連線(control + file)→ 誰都連不完整 |
| `--max-per-ip 0` | `0` 是**不限**不是「全擋」。想收緊卻打成 0 = 把這道防線關掉 |
| 以為 `--max-conns 256` 是 256 人 | 那是**連線數**;一份 client 兩條 → 大約 128 人 |
| `--password "  "`(全空白) | 會被 Trim 成空字串 = **不檢查密碼**(`--tls-pass` 反而刻意不 Trim —— 尾端空白可能是密碼的一部分) |
| 只給 `--tls-pass` 忘了 `--tls-cert` | **不會報錯**,安靜地以明文啟動(只有 `--tls-pass-file` 有配對檢查) |
| `--port=27015`(等號寫法) | 不支援 → 「不認得的選項」。值一律是下一個參數 |
| 想用 `--upload-mb-hour` 擋一整段時間的量 | 額度是**整點桶**不是滑動視窗:跨過整點就歸零 → 整點前後可以連傳兩倍 |
| client `serverAddress=1.2.3.4 # 家裡` | `config.ini` **不支援行內註解** → 整串(含 `# 家裡`)被當成主機名 |
| client `serverPort=0` | 夾成 `1`(不會回退 27015)→ 連不上,而且沒有任何錯誤訊息 |
| client `serverTls=y` | 只認 `1/true/yes/on` 與 `0/false/no/off`,`y` 被靜默忽略 → 維持關閉 |

### client(`<DataRoot>\PROFILE\config.ini` 的 `[Net]` 區)

| 鍵(**大小寫敏感**) | 預設 | 作用 |
|---|---|---|
| `serverAddress` | 空 | ★ **總開關**。留空 = 純單機(連線層一行都不會建起來,體驗與加連線之前完全一樣) |
| `serverPort` | `27015` | 夾在 1..65535 |
| `serverPassword` | `abab123` | 要與 server 的 `--password` 一致。兩邊的預設值指向同一個常數,所以「都不改」就連得上 |
| `serverToken` | 空 | server 有 `--tokens` 時才需要 |
| `serverTls` | `0` | `1` = 加密。server 有 `--tls-cert` 時**必須**設 1 |
| `serverCertFingerprint` | 空 | 釘選的憑證指紋。自簽必填;冒號/空白/大小寫隨便貼(會被正規化成 64 個小寫 hex),**格式不對就當沒填** |
| `netAutoDownload` | `1` | 缺歌時自動下載。旁觀者一律不自動下載 |
| `netMaxDownloadMb` | `200` | 自動下載的單首歌上限 MB(夾在 1..2048) |

`config.ini` 的位置:`<DataRoot>\PROFILE\config.ini`,而 `<DataRoot>` 受 `SDO_DATA_ROOT`
環境變數與 repo 根的 `data_root.txt` 影響。**舊的 config.ini 少了新鍵不用手動補** ——
開機時會自動補上預設值並回寫。

其他固定值(不可設定,寫在 `Sdo.Net/NetLimits.cs`):房間 6 個座位 + 最多 10 個旁觀、
ping 5 秒一次 / 15 秒沒收到視為斷線、載入逾時 30 秒、分數流 client 5 Hz→server 彙整 5 Hz 推回、
訊息速率上限 control 32/秒、frame 20/秒、聊天 3 秒 5 則。

---

## 6. 測試怎麼跑

```bash
# server 與協定的單元測試(零 Unity,約 6 秒)—— 目前 413 條
dotnet test server/
```

```powershell
# Unity EditMode —— 約 1,900 條,35 秒左右。看的是「failed=0」而不是總數:
# 有幾個 fixture 是拿 DATA 裡的真實素材跑的資料驅動測試,總數會隨手上的 DATA 內容浮動
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" -batchmode -runTests `
  -projectPath "H:\65_remake-online\65\My project" -testPlatform EditMode `
  -testResults "H:\65_remake-online\results.xml" -force-d3d11 -logFile "H:\65_remake-online\test.log"
```

🔴 **絕對不要加 `-nographics`** —— 約 20 個渲染測試會假紅。
🔴 **exit code 會騙人**,結果要看 `results.xml` 的 `<test-run ... passed="" failed="">`。
🔴 Unity 開著的時候跑不了(Library 被鎖),要先關掉。

實機驗證見 §2。

### 截圖驗證(UI 改動)

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
DataRoot 看 repo 根的 `data_root.txt`,**不是** exe 旁邊那份 DATA
(`log.txt` 第一行會印出實際用的那個)。

### dev 環境變數(打包版讀環境變數;編輯器讀 EditorPrefs)

| 變數 | 作用 |
|---|---|
| `SDO_DATA_ROOT` | 換一份 DATA(同機兩開**必須**) |
| `SDO_VERBOSE=1` | 讓 `Debug.Log` 進 log 檔。🔴 **打包版預設把 info 全丟掉**,查連線問題一定要開 |
| `SDO_LOG=<路徑>` | log 寫到指定檔案。🔴 同機兩開**必須**各給一個(見 §2) |
| `SDO_ROOM=1` | 開機直接開一間房 |
| `SDO_JOINFIRST=1` | 開機直接加入 server 上人最多的那間房 |
| `SDO_JOINDLG=1` | 開機直接彈「輸入房號」框(只是要看排版) |
| `SDO_AUTOREADY=1` | 進房自動按準備 |
| `SDO_AUTOSTART=1` | 房主自動開始(線上會等第二個人坐下) |
| `SDO_AUTOPLAY=1` | 自動打完整首 |
| `SDO_PICKSONG=<片段>` | 房主自動選第一首名字含這段字的**外部歌** |
| `SDO_CLOSESEATS=1` | 房主把自己以外的座位全關掉(做出滿房,驗自動轉旁觀) |
| `SDO_DANCERS=<n>` / `SDO_ROOMAVATARS=<n>` | 效能量測:場上 / 房間補到 n 隻角色 |

---

## 7. 症狀 → 原因 → 去哪裡看

log 在哪:server 是 stdout(systemd → `journalctl -u sdo-server -f`;
用 §4.5b 的 tmux 那套 → `sdoctl log`,檔案在 `<data>/server.log`);
client 是 `<exe 目錄>\log.txt`(或 `SDO_LOG` 指定的路徑),**要先開 `SDO_VERBOSE=1`**。

| 玩家/你看到什麼 | 真正的原因 | 去哪裡確認 |
|---|---|---|
| 開機提示「連不上伺服器,改用單機模式」 | 位址/port 不對、server 沒開、防火牆擋住。TCP 連不到約 5 秒放棄,開機最多等 6 秒(蓋住「連上了但握手沒回」)就退回單機,不會卡住 | client log 的 `[net] 連不上伺服器` 後面那句 |
| 「密碼不符 —— 請確認 config.ini…」 | 兩邊 `--password` / `serverPassword` 不一致 | server log `連線 #N 密碼不符,拒絕(client 送的是空值/另一個值)`(**不會印密碼本體**) |
| 「伺服器不認得這個 token」 | `serverToken` 沒填或不在 token 檔裡 | server log `連線 #N token 認證失敗,拒絕` |
| 連不上,server log 有「TLS 握手失敗」 | client `serverTls=0` 但 server 開了 TLS(或反過來) | 兩邊的 `serverTls` / `--tls-cert` |
| 「憑證驗證失敗… 自簽憑證要在 config.ini 填 serverCertFingerprint」 | 自簽憑證但沒貼指紋 | 抄 server 開機印的那 64 字 |
| 「憑證指紋不符(設定的是 xxx…,收到的是 yyy…)」 | 指紋貼錯,或真的換了憑證 | 訊息帶兩邊前 16 碼,直接對照 |
| 「協定版本不合」 | 遊戲與 server 版本不同 | 兩邊都要更新(協定改動會 +1) |
| 房間滿了進不去 | 座位 6 + 旁觀 10 都滿了(座位滿會自動改用旁觀身分進去) | server log `以旁觀身分進入房 N` 有沒有出現 |
| 缺歌一直卡在「下載中」 | 單首超過 `netMaxDownloadMb`、或 server 的上傳配額擋住 | server log `上傳被拒`/`配額`;client log `[net] 傳檔失敗:…` |
| 有人卡住開不了場 | 載入逾時 30 秒後會逐出那個人照樣開場 | server log `載入逾時,逐出本場` / `沒有人載入成功,本場取消` |
| 房間一直停在「遊戲中」 | 有人的 `playFinished` 沒送到(斷線/當掉) | server log 有沒有 `第 N 場結算` |
| 按了某個鈕**完全沒反應** | server 回了 error 但畫面沒說 —— 這類現在會跳 Toast 了 | client log `[net] server error: <code>` |

🔴 **靜默失敗要特別警覺**:這個連線層刻意**不做樂觀更新**(按了不先改畫面,一律等 server 的
快照),所以「按了沒反應」通常代表 server 拒絕了。看 client log 的 `[net] server error:` 那行。

---

## 8. 日常維運

**磁碟。** 只有一個地方會長大:`<data>/blobs/`

```
blobs/files/<sha 前2碼>/<sha>    檔案本體(內容尋址 → 同一首歌只存一份)
blobs/packs/<packId>.json        這首歌由哪些檔案組成 + 最後使用時間
blobs/tmp/<uploadId>/            上傳暫存(收完驗過才原子搬進 files/;開機會清掉殘留的)
```

清理是自動的:每 15 分鐘掃一次,把「沒有任何存活房間正在用」且超過 `--ttl-hours` 沒被用過的
pack 刪掉,再刪掉引用計數歸零的檔案本體;總量超過 `--max-blob-gb` 時會連比較舊的一起刪。
🔴 判斷「多久沒用」看的是 `packs/*.json` 裡自己記的 `lastUsedUtc`,**不是檔案系統的 atime**
(Linux 常掛 `noatime`,那個時間會騙人)。
**不需要手動清**,也不要自己去刪 `files/` 底下的東西(那會讓 pack 指到不存在的檔案)。

**備份。** 沒有需要備份的東西 —— 房間是暫時的(開房→打歌→散),歌曲暫存最多留一天,
玩家的角色/戰績/設定全都在**各自的本機**。要保的只有 `/etc/sdo/` 那三個檔(憑證、密碼、token)。

**升級。** 換執行檔 → `systemctl restart sdo-server`。協定版本有改的話**兩邊要一起換**
(版本不合會明確擋掉,不會半殘地跑)。

**關服。** `systemctl stop`(SIGTERM)會把連線乾淨收掉。玩家那邊會看到連線中斷、退回單機。

---

## 相關

- [README.md](README.md) —— 這台 server 是什麼、為什麼這樣設計、csproj 為什麼直接編遊戲的原始碼
- [../docs/systems/net-protocol.md](../docs/systems/net-protocol.md) —— 每一個訊息、每一條房間規則
- [../docs/systems/net-song-transfer.md](../docs/systems/net-song-transfer.md) —— 缺歌自動傳檔
- [../docs/systems/networking.md](../docs/systems/networking.md) —— 傳輸層、執行緒模型、診斷
