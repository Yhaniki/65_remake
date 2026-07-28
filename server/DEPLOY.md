# 部署到 srcds.yhaniki.com(port 27017)

把 `sdo-server` 部署到 `srcds.yhaniki.com`(`35.221.224.91`,GCP `asia-east1-c`)的實作手冊。
通用的參數說明、協定、安全性設計在 [README.md](README.md);這份只講**這一台**怎麼上線。

---

## 0. 先讀:這台主機的既成事實

每一條都會影響下面某個步驟,不是背景資訊。**2026-07-28 實機盤點**。

| 事實 | 對部署的影響 |
|---|---|
| **外部只通 TCP 22 與 27017** | 我們**只能**用 27017。27015/27016/27018~27020/27099/8080 實測全部 BLOCKED |
| 27017 是當初開給 L4D2 的規則(TCP+UDP 都放) | 我們的協定是裸 TCP → 剛好吃得到,**GCP 防火牆一行都不用改** |
| L4D2 也用 27017 | **撞 port,兩個不能同時跑**。見 [§9](#9-跟-l4d2-的互斥) |
| 擋在 GCP VPC 層,不是機器上 | 機器上 `ufw` 是 **inactive**、`iptables` INPUT policy 是 ACCEPT。⚠️ `~/.bash_history` 裡有 `sudo ufw allow ...` 那類指令,但 ufw 沒開 → **那些規則一行都沒生效**,別以為機器上開過就是開了 |
| **磁碟 96G 只剩 21G(79% 已用)** | `--max-blob-gb` **不能用預設值**,見 [§6](#6-systemd-unit) |
| 沒裝 dotnet | publish 一定要 `--self-contained true`,不能靠機器上的 runtime |
| Ubuntu 24.04.2 / x86_64 / glibc 2.39 | `-r linux-x64` 直接跑得動 |
| sudo 要打密碼(不是免密) | 每個 `sudo` 都會問密碼,無法無人值守 |
| **不是專用機** | 上面跑著 palworld(docker)、vsftpd、samba、ZeroTier。**別動它們** |
| 2 core / 7.8G RAM(已用 3.3G) | 對這個 server 綽綽有餘 |

上傳實測頻寬 **2.5 MB/s**,publish 出來約 64MB → 傳一次約 26 秒。

---

## 1. 本機:publish

在 `H:\65_remake-online`:

```powershell
dotnet publish server/Sdo.Server -c Release -r linux-x64 `
  --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false `
  -o server/Sdo.Server/bin/publish-linux
```

產出 `server\Sdo.Server\bin\publish-linux\sdo-server`(單一檔,自帶 .NET runtime)。

**不要加 `-p:PublishTrimmed=true`** —— 省下的體積不值得 trim 那類「執行期才炸、而且只在某條路徑上炸」的問題。

上傳前先讓測試綠燈,server 掛掉的成本比在本機多跑 30 秒高得多:

```powershell
dotnet test server/
```

---

## 2. 本機:上傳

```powershell
scp server/Sdo.Server/bin/publish-linux/sdo-server yhaniki@srcds.yhaniki.com:/tmp/sdo-server
```

之後每次改版都是重跑 §1 + §2,再跳到 [§10 更新流程](#10-更新流程改版重傳)。

---

## 3. server:帳號與目錄(只做一次)

```bash
ssh yhaniki@srcds.yhaniki.com
```

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin sdo
sudo mkdir -p /opt/sdo-server /var/lib/sdo-server /etc/sdo
sudo chown sdo:sdo /var/lib/sdo-server
sudo chmod 750 /etc/sdo

# 執行檔就位
sudo install -o root -g root -m 755 /tmp/sdo-server /opt/sdo-server/sdo-server
rm -f /tmp/sdo-server

# 確認跑得起來(能印出說明就代表 self-contained 沒問題)
/opt/sdo-server/sdo-server --help | head -5
```

用獨立的 `sdo` 系統帳號跑,而不是 `yhaniki`:這台上面有別人的東西(palworld、L4D2、samba),
server 被打穿時的影響範圍要限制在它自己的資料目錄裡。

---

## 4. server:TLS 憑證

**憑證在 server 上產,不要在本機產完再傳** —— 私鑰不必經過網路,而且那台就有 openssl。

```bash
# 自簽,十年份(免得忘記換)。CN/SAN 要跟 client 填的 serverAddress 一模一樣。
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
  -keyout /tmp/key.pem -out /tmp/cert.pem \
  -subj "/CN=srcds.yhaniki.com" \
  -addext "subjectAltName=DNS:srcds.yhaniki.com"

# 合成 server 要的 pfx(不設密碼 —— 檔案已經是 600 且只有 sdo 讀得到)
openssl pkcs12 -export -out /tmp/cert.pfx -inkey /tmp/key.pem -in /tmp/cert.pem -passout pass:

sudo install -o sdo -g sdo -m 600 /tmp/cert.pfx /etc/sdo/cert.pfx

# 記下指紋 —— 每個玩家的 config.ini 都要填這一串
openssl x509 -in /tmp/cert.pem -noout -fingerprint -sha256

# 私鑰與中間檔清掉
rm -f /tmp/key.pem /tmp/cert.pem /tmp/cert.pfx
```

指紋長這樣(冒號與大小寫都可以直接貼進 config.ini,client 會正規化):

```
sha256 Fingerprint=6D:6E:...:22
```

server 開機時也會再印一次,以開機那次為準。

> **為什麼一定要填指紋:** 自簽憑證沒有 CA 背書,一般驗證必定失敗。最容易犯的錯是在 client 的
> 驗證 callback 裡直接放行 —— 那樣 TLS 只剩裝飾,任何人都能插一台假 server,加密照樣成立,
> 只是加密給攻擊者。填了指紋之後 client 只認指紋一模一樣的那張憑證。

---

## 5. server:token

一人一把,綁 `playerId` 才真的能阻止冒用(只給 token 不綁 id = 認證過了但身分還是 client 自稱的)。

```bash
# 產一把:openssl rand -hex 16
sudo tee /etc/sdo/tokens.txt > /dev/null <<'EOF'
# 一行一個。格式:<token> = <playerId>, <顯示名稱>, <admin>
# playerId 是 8 位數字,對應 DATA/PROFILE/<id>/
a1b2c3d4e5f60718a1b2c3d4e5f60718 = 00000000, 房主, admin
11223344556677881122334455667788 = 00000001, 玩家2
EOF

sudo chown sdo:sdo /etc/sdo/tokens.txt
sudo chmod 600 /etc/sdo/tokens.txt
```

token 少於 16 字元的**那一行**會被忽略(短 token 是可以猜的),開機時每被忽略一行記一句 log
(只寫第幾行,不印 token 本體)。

🔴 是「忽略那一行」,不是「server 開不起來」。所以如果檔案裡的 token 全都太短、
或這個檔**根本讀不到**(路徑打錯/權限不足),有效 token 數會變成 0 →
**認證等於沒啟用**(不是「誰都進不來」),而 server 照常跑起來。
這與 `--tls-cert` 找不到就拒絕啟動剛好相反 —— 所以第 7 節那個開機檢查不能跳過。

> ⚠️ token 是共享機密,拿到就是那個身分。所以 TLS 是搭配條件不是選項:明文連線上的 token 等於公開的。

---

## 6. systemd unit

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
ExecStart=/opt/sdo-server/sdo-server \
  --port 27017 \
  --data /var/lib/sdo-server \
  --password 換成你自己的密碼 \
  --tls-cert /etc/sdo/cert.pfx \
  --tokens /etc/sdo/tokens.txt \
  --max-per-ip 4 \
  --upload-mb-hour 512 \
  --max-blob-gb 5 \
  --ttl-hours 24
Restart=on-failure
RestartSec=5

KillSignal=SIGTERM
TimeoutStopSec=10

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/sdo-server

[Install]
WantedBy=multi-user.target
```

四個參數為什麼是這些值:

| 參數 | 值 | 理由 |
|---|---|---|
| `--port` | **27017** | 這台外部**只通**這個 port(和 22)。填 27015 會變成「server 有在跑但誰都連不進來」 |
| `--max-blob-gb` | **5** | 磁碟只剩 21G,而且是跟 palworld/L4D2 共用的同一顆。預設值會在 TTL 到期前先把磁碟塞爆,連累別人的服務 |
| `--upload-mb-hour` | 512 | 擋「拿 server 當免費網路硬碟」。四個人各傳一輪歌綽綽有餘 |
| `--max-per-ip` | 4 | 一份 client 正常吃 2 條(control + file),所以 4 = 同一個 IP 兩份 client |

`--password` 記得換掉:預設值 `abab123` 是寫在公開原始碼裡的。

> ⚠️ **`--password` 會出現在 `ps` / `/proc` 裡**,同一台機器上任何帳號都看得到(這台有別的使用者)。
> 沒有 `--password-file` 這個選項,所以真正的身分保證要靠 `--tokens`,`--password` 只當進站門檻。

啟用:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now sdo-server
```

---

## 7. 確認開機那幾行

```bash
journalctl -u sdo-server -n 40 --no-pager
```

**要看到 TLS 指紋、而且沒有任何 `⚠️` 開頭的行**:

```
[sdo-server] TLS 已啟用(TLS 1.2/1.3)。憑證指紋 SHA-256:
[sdo-server]   6473b40678340324aa73a7cd6144d2168cdbc24f3d3a04ef469a672cc2c92e22
```

看到這兩行的任何一行,就是**還沒準備好開公網**,回去補 §4 / §5:

```
[sdo-server] ⚠️  沒有加密(明文 TCP)。要加密請給 --tls-cert <pfx>。
[sdo-server] ⚠️  沒有帳號認證 —— 身分由 client 自稱。要認證請給 --tokens <file>。
```

憑證讀不到 / 沒有私鑰 / pfx 密碼錯 → **server 直接開不起來(exit 4)**,不會偷偷退回明文。
`systemctl status sdo-server` 會顯示那個 exit code。

---

## 8. 從外面驗證 port 真的通

在 server 上 `ss -tlnp | grep 27017` 只能證明它在聽,**證明不了外面進得來**。要從這台電腦測:

```powershell
$c = New-Object System.Net.Sockets.TcpClient
$ar = $c.BeginConnect('srcds.yhaniki.com', 27017, $null, $null)
if ($ar.AsyncWaitHandle.WaitOne(5000)) { $c.EndConnect($ar); "REACHABLE: $($c.Connected)" } else { "BLOCKED" }
$c.Close()
```

`Test-NetConnection -ComputerName srcds.yhaniki.com -Port 27017` 也可以,慢一點但輸出好讀。

---

## 9. 跟 L4D2 的互斥

L4D2 不是 systemd service,是登入後手動跑的(`~/.bash_history` 裡一整排):

```bash
cd ~/L4D2 && ./srcds_run -port 27017 +sv_setmax 31 -game left4dead2 +map c1m1_hotel
```

**兩個都綁 27017,不能同時跑。** 誰先綁誰贏,後啟動的那個會 bind 失敗。

要玩 L4D2 之前:

```bash
sudo systemctl stop sdo-server
```

玩完:

```bash
sudo systemctl start sdo-server
```

嫌煩就照 [§11](#11-之後搬到自己的-port) 搬到專屬 port。

---

## 10. 更新流程(改版重傳)

```powershell
# 本機
dotnet test server/
dotnet publish server/Sdo.Server -c Release -r linux-x64 `
  --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false `
  -o server/Sdo.Server/bin/publish-linux
scp server/Sdo.Server/bin/publish-linux/sdo-server yhaniki@srcds.yhaniki.com:/tmp/sdo-server
```

```bash
# server
sudo systemctl stop sdo-server
sudo install -o root -g root -m 755 /tmp/sdo-server /opt/sdo-server/sdo-server
rm -f /tmp/sdo-server
sudo systemctl start sdo-server
journalctl -u sdo-server -n 20 --no-pager
```

執行檔在跑的時候不能直接覆蓋(`Text file busy`),所以一定要先 `stop`。

歌曲暫存要清掉的話(TTL 沒到但想手動清):

```bash
sudo systemctl stop sdo-server
sudo rm -rf /var/lib/sdo-server/blobs/*
sudo systemctl start sdo-server
```

---

## 11. client 端設定

每個玩家的 `<DataRoot>\PROFILE\config.ini`。DataRoot 看 repo 根的 `data_root.txt`,
**不是** exe 旁邊那份 DATA(`log.txt` 第一行會印出實際用的那個)。

```ini
[Net]
serverAddress=srcds.yhaniki.com
serverPort=27017
serverPassword=你在 §6 設的那個
serverToken=這個人專屬的那一把
serverTls=1
serverCertFingerprint=§4 印出來的那 64 個 hex
netAutoDownload=1
netMaxDownloadMb=200
```

- `serverAddress` 吃 hostname(client 走 `TcpClient.BeginConnect(host, port)`),不必填 IP。
  填 hostname 的好處是 GCP 換 IP 時不用改每個人的設定。
- `serverPort` **一定要改成 27017**,預設值是 27015 —— 忘了改的症狀是「連線逾時」,
  而那個訊息不會告訴你是 port 沒改。
- `serverTls=1` 不能少:server 開了 TLS 而 client 沒開 → 連不上,**絕不會退回明文**。
- 每個人的 `serverToken` 不一樣,`serverPassword` / `serverCertFingerprint` 全部一樣。

### 常見症狀對照

| 症狀 | 原因 |
|---|---|
| 連線逾時 | `serverPort` 忘了改 27017 / server 沒在跑 / L4D2 佔著 port |
| 密碼不符 | `serverPassword` 與 `--password` 不一致 |
| 伺服器不認得這個 token | `serverToken` 沒填或不在 `/etc/sdo/tokens.txt` 裡 |
| 憑證驗證失敗…自簽憑證要填 serverCertFingerprint | `serverCertFingerprint` 沒填 |
| 憑證指紋不符(設定的是 xxx…,收到的是 yyy…) | 重新產過憑證但沒更新 client。訊息帶兩邊前 16 碼,好對照 |
| TLS 握手失敗 | `serverTls` 與 server 的 `--tls-cert` 不一致 |
| 進遊戲直接是單機版面(兩顆鈕而不是三顆) | `serverAddress` 是空的 → 連線層根本沒建起來 |

連不上時 client 會提示並**自動退回單機**,不會卡在開機畫面 —— 所以「看起來能玩」不代表連上了,
以選男女畫面是三顆鈕還是兩顆鈕為準。

---

## 12. 之後搬到自己的 port

想跟 L4D2 井水不犯河水,就去 GCP Console 加一條規則(VM 內沒有權限做這件事 ——
它的 service account 沒有 compute scope):

```bash
gcloud compute firewall-rules create sdo-server \
  --network default --direction INGRESS --priority 1000 \
  --action ALLOW --rules tcp:27015 --source-ranges 0.0.0.0/0
```

⚠️ **不要加 `--target-tags`**。那台 VM 的 network tags 是空的 `[]`,綁了 tag 規則就不會套到它
(而症狀是「規則明明建好了卻還是連不進來」)。要綁的話得先給 VM 加 tag。

然後改三個地方,缺一不可:

1. unit 檔的 `--port 27017` → `--port 27015`,`sudo systemctl daemon-reload && sudo systemctl restart sdo-server`
2. 每個玩家 config.ini 的 `serverPort`
3. 照 §8 從外面驗證新 port 真的通,**再**通知大家改設定

### 另一條路:ZeroTier

那台已經在 ZeroTier 網路 `Yhaniki`(`a84ac5c10a080dad`),server 端 IP `172.22.38.132`。
玩的人都在這個網路裡的話,`serverAddress=172.22.38.132` 就通了,GCP 完全不用碰,
而且不曝露在公網。代價是每個玩家都要入網。

---

## 13. 疑難排解

**server 起不來**

```bash
systemctl status sdo-server        # 看 exit code
journalctl -u sdo-server -n 50 --no-pager
```

| exit code | 意思 |
|---|---|
| 2 | 參數錯誤(unit 檔的 `ExecStart` 打錯,stderr 會印出完整說明) |
| 3 | 建不出 `--data` 目錄(`/var/lib/sdo-server` 的 owner 不是 `sdo`?) |
| 4 | TLS 設定有問題 —— 憑證讀不到 / 沒有私鑰 / pfx 密碼錯。**它拒絕以明文啟動** |

**外面連不進來,但 `ss` 顯示有在聽**

1. `--bind` 是不是被寫成 `127.0.0.1`?(預設 `0.0.0.0` 才對外)
2. port 是不是 27017?這台**只有** 22 和 27017 通
3. L4D2 是不是正佔著 27017?`sudo ss -tulnp | grep 27017`

**磁碟滿了**

```bash
df -h /
du -sh /var/lib/sdo-server/blobs
```

這台是跟 palworld / L4D2 共用同一顆磁碟的,塞爆會連累別人。`--max-blob-gb 5` 是上限不是配額,
真的吃緊就往下調,或縮 `--ttl-hours`。

**看每一筆收發的訊息**

unit 的 `ExecStart` 加 `-v` 再 restart。訊息量很大,查完記得拿掉。
