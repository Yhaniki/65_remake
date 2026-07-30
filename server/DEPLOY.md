# 部署到 srcds.yhaniki.com(port 8888,tmux + sdo 帳號)

把 `sdo-server` 部署到 `srcds.yhaniki.com`(`35.221.224.91`,GCP `asia-east1-c`)的實作手冊。
通用的參數說明、協定、安全性設計在 [README.md](README.md);這份只講**這一台**怎麼上線。

跑法是 **tmux + 獨立的 `sdo` 系統帳號**,用 [`deploy/sdoctl.sh`](deploy/sdoctl.sh) 這支腳本開關。
為什麼不用 systemd(以及因此少了什麼)見 [§14](#14-為什麼是-tmux-不是-systemd)。

## 最短路徑

[`deploy/bootstrap.sh`](deploy/bootstrap.sh) 把 §3~§8 一次做完(sudo 密碼只問一次,
token 自動產、指紋自動抓,最後印出每個玩家要填的 `config.ini`):

```powershell
# 本機(§1 publish 之後)
scp server/Sdo.Server/bin/publish-linux/sdo-server yhaniki@srcds.yhaniki.com:/tmp/sdo-server
scp server/deploy/sdoctl.sh                        yhaniki@srcds.yhaniki.com:/tmp/sdoctl
scp server/deploy/bootstrap.sh                     yhaniki@srcds.yhaniki.com:/tmp/bootstrap.sh
```

```bash
# server
chmod +x /tmp/bootstrap.sh
/tmp/bootstrap.sh --players 4
```

跑完還有兩件事要自己做:[§9 從外面驗證 port](#9-從外面驗證-port-真的通)、
[§11 開機自動啟動](#11-開機自動啟動與免密碼可選)。

**可以重複跑** —— 已經存在的憑證 / token / 設定檔一律不動(那三個一換掉,所有玩家的
`config.ini` 都得跟著改)。要重產就先 `sudo rm` 掉那個檔再跑。

下面 §3~§8 是它到底做了什麼:想手動做、或它中途停掉要查原因的時候看。

---

## 0. 先讀:這台主機的既成事實

每一條都會影響下面某個步驟,不是背景資訊。**2026-07-28 實機盤點**。

| 事實 | 對部署的影響 |
|---|---|
| **外部通 TCP 22、27017,以及後來加開的 8888(TCP+UDP)** | 我們用 **8888**。27015/27016/27018~27020/27099/8080 實測全部 BLOCKED |
| 8888 是後來才加的規則,**沒有實測過** | 27017 是當初開給 L4D2 而確定會通的;8888 只是「規則建好了」。[§9](#9-從外面驗證-port-真的通) 一定要做 |
| 我們的協定是**裸 TCP** | 只吃 8888 那條 TCP 規則,UDP 開著不影響也用不到 |
| 8888 跟 L4D2 的 27017 不衝突 | 兩個可以同時跑。(用 27017 的舊版寫法會撞 port,現在沒這問題了) |
| 擋在 GCP VPC 層,不是機器上 | 機器上 `ufw` 是 **inactive**、`iptables` INPUT policy 是 ACCEPT。⚠️ `~/.bash_history` 裡有 `sudo ufw allow ...` 那類指令,但 ufw 沒開 → **那些規則一行都沒生效**,別以為機器上開過就是開了 |
| VM 的 network tags 是空的 `[]` | GCP 防火牆規則**不能綁 `--target-tags`**,綁了就不會套到這台(症狀是「規則明明建好了卻連不進來」) |
| **磁碟 96G 只剩 21G(79% 已用)** | `--max-blob-gb` **不能用預設值**,見 [§7](#7-server裝-sdoctl-與設定檔) |
| 沒裝 dotnet | publish 一定要 `--self-contained true`,不能靠機器上的 runtime |
| Ubuntu 24.04.2 / x86_64 / glibc 2.39 | `-r linux-x64` 直接跑得動 |
| sudo 要打密碼(不是免密) | 每個 `sdoctl` 操作都會問一次密碼,除非照 [§11](#11-開機自動啟動與免密碼可選) 設 sudoers |
| **不是專用機** | 上面跑著 palworld(docker)、vsftpd、samba、ZeroTier、L4D2。**別動它們** |
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
scp server/deploy/sdoctl.sh                        yhaniki@srcds.yhaniki.com:/tmp/sdoctl
scp server/deploy/bootstrap.sh                     yhaniki@srcds.yhaniki.com:/tmp/bootstrap.sh
```

之後每次改版都是重跑 §1 + §2,再跳到 [§12 更新流程](#12-更新流程改版重傳)。

> ⚠️ **`bootstrap.sh` 必須是 LF 換行。** repo 根的 `.gitattributes` 已經釘死
> (`*.sh text eol=lf`),但從別的地方複製過去的話,傳上去會變成
> `bad interpreter: /usr/bin/env bash^M` 或一整排語法錯誤。補救:
> `sed -i 's/\r$//' /tmp/bootstrap.sh`。
> (`sdoctl` 不用管 —— `bootstrap.sh` 安裝它的時候會順手把 CR 洗掉。)

---

## 3. server:帳號與目錄(只做一次)

```bash
ssh yhaniki@srcds.yhaniki.com
```

```bash
sudo apt install -y tmux

sudo useradd --system --create-home --home-dir /var/lib/sdo-server --shell /bin/bash sdo
sudo mkdir -p /opt/sdo-server /etc/sdo
sudo chown sdo:sdo /var/lib/sdo-server
sudo chmod 750 /var/lib/sdo-server
sudo chmod 755 /etc/sdo
```

四件事值得說明:

- **為什麼用獨立的 `sdo` 帳號**:這台上面有別人的東西(palworld、L4D2、samba、你的家目錄),
  server 被打穿時的影響範圍要限制在它自己的資料目錄裡。
- **`--shell /bin/bash` 不能寫成 `/usr/sbin/nologin`**:tmux 是拿 passwd 裡的 shell 去跑命令的,
  `nologin` 會讓 session 建起來就死。這個帳號沒有密碼(`useradd --system` 不設)、也沒有
  SSH key,登不進來 —— 給 shell 只是 tmux 的硬性需求。
- **`--home-dir` 直接指向資料目錄**:tmux 需要一個寫得進去的 `HOME`,順便就是 `--data`。
- 🔴 **`/etc/sdo` 是 755,不是 750。** 目錄權限管的是「誰穿得過去」,而 `sdo`(要讀憑證與
  token)和 `yhaniki`(要讀 `sdoctl.conf`)**都不在 root group**。設成 `750 root:root`
  的話兩邊都進不去,而症狀是 server 說 **「`--tls-cert` 找不到憑證檔」** —— 看起來像檔案沒產出來,
  實際上是權限。內容的保護在檔案本身:`cert.pfx` / `tokens.txt` 是 600 `sdo`,`sdoctl.conf` 是 640。

🔴 **不要把東西留在 `~/dance_server_data`。** `/home/yhaniki` 是 750,`sdo` 進不去;
要讓它進得去就得 `chmod o+x` 家目錄 —— 那等於為了隔離而先把隔離拆掉。

---

## 4. server:執行檔就位

```bash
sudo install -o root -g root -m 755 /tmp/sdo-server /opt/sdo-server/sdo-server
rm -f /tmp/sdo-server

# 確認跑得起來(能印出說明就代表 self-contained 沒問題)
/opt/sdo-server/sdo-server --help | head -5
```

owner 是 `root`、`sdo` 只有讀+執行 —— server 被打穿也改不掉自己的執行檔。

> 執行檔如果已經在 `~/dance_server_data/sdo-server`,把上面第一行的來源換成那個路徑,
> 裝完再 `rm ~/dance_server_data/sdo-server`。

---

## 5. server:TLS 憑證

**憑證在 server 上產,不要在本機產完再傳** —— 私鑰不必經過網路,而且那台就有 openssl。

```bash
cd /tmp

# 自簽,十年份(免得忘記換)。CN/SAN 要跟 client 填的 serverAddress 一模一樣。
openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
  -keyout key.pem -out cert.pem \
  -subj "/CN=srcds.yhaniki.com" \
  -addext "subjectAltName=DNS:srcds.yhaniki.com"

# 合成 server 要的 pfx(不設密碼 —— 檔案已經是 600 且只有 sdo 讀得到)
openssl pkcs12 -export -out cert.pfx -inkey key.pem -in cert.pem -passout pass:

sudo install -o sdo -g sdo -m 600 cert.pfx /etc/sdo/cert.pfx

# 記下指紋 —— 每個玩家的 config.ini 都要填這一串
openssl x509 -in cert.pem -noout -fingerprint -sha256

# 私鑰與中間檔清掉
rm -f key.pem cert.pem cert.pfx
```

指紋長這樣(冒號與大小寫都可以直接貼進 config.ini,client 會正規化):

```
sha256 Fingerprint=6D:6E:...:22
```

server 開機時也會再印一次,以開機那次為準(`sdoctl start` 會直接顯示)。

> **為什麼一定要填指紋:** 自簽憑證沒有 CA 背書,一般驗證必定失敗。最容易犯的錯是在 client 的
> 驗證 callback 裡直接放行 —— 那樣 TLS 只剩裝飾,任何人都能插一台假 server,加密照樣成立,
> 只是加密給攻擊者。填了指紋之後 client 只認指紋一模一樣的那張憑證。

---

## 6. server:token

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

⚠️ 上面兩把是**文件裡的範例值**,一定要用 `openssl rand -hex 16` 重產。

token 少於 16 字元的**那一行**會被忽略(短 token 是可以猜的),開機時每被忽略一行記一句 log
(只寫第幾行,不印 token 本體)。

🔴 是「忽略那一行」,不是「server 開不起來」。所以如果檔案裡的 token 全都太短、
或這個檔**根本讀不到**(路徑打錯/權限不足),有效 token 數會變成 0 →
**認證等於沒啟用**(不是「誰都進不來」),而 server 照常跑起來。
這與 `--tls-cert` 找不到就拒絕啟動剛好相反 —— 所以 [§8](#8-啟動並確認) 那個開機檢查不能跳過。

> ⚠️ token 是共享機密,拿到就是那個身分。所以 TLS 是搭配條件不是選項:明文連線上的 token 等於公開的。

### 不想一人發一把:關掉就好

發 token 給每個人、每個人各自貼進 `config.ini` —— 朋友之間玩這個成本常常不划算。
`sdoctl.conf` 裡把 `TOKENS` 留空就不啟用(`sdoctl` 會整個不帶 `--tokens`):

```bash
echo 'TOKENS=' | sudo tee -a /etc/sdo/sdoctl.conf
sdoctl restart
```

client 那邊 `serverToken=` 留空即可,其他照舊。

**換來什麼、失去什麼:**

| | 關掉 token 之後 |
|---|---|
| 進站門檻 | 只剩 `--password`。**密碼變成唯一的一道門** —— 別再用 `abab123` |
| 身分 | `playerId` 與名字由 client 自稱。任何人可以叫任何名字、頂任何 playerId |
| 加密 | 不受影響,TLS 照樣開著 |
| per-IP / 上傳配額 | 不受影響 |

🔴 **兩個人 `activeId` 撞在一起不會出事。** server 認的是自己發的流水號 `UserId`
(`Hub.Handlers.cs` 的 `conn.UserId = _nextUserId++`),房間、座位、傳檔配額全用它;
傳檔連線靠 `sessionKey` 認親。`playerId` 只是跟著顯示的資料 ——
兩個人都是 `00000000`(預設的女角)照樣各玩各的,不會互踢也不會混線。
會混淆的只有你自己看名字牌的時候。

開機橫幅會出現 `⚠️ 沒有帳號認證`,`sdoctl start` 也會再提醒一次 ——
那是**正確**的提醒(你確實在公網上關掉了認證),不是壞掉。

---

## 7. server:裝 sdoctl 與設定檔

```bash
sudo install -o root -g root -m 755 /tmp/sdoctl /usr/local/bin/sdoctl
rm -f /tmp/sdoctl
sdoctl --help
```

腳本本身**不含任何機密**,所以可以 755、可以直接覆蓋更新。機密與這台的參數放設定檔:

```bash
sudo tee /etc/sdo/sdoctl.conf > /dev/null <<'EOF'
# sdoctl 的設定。這裡沒寫到的都用 /usr/local/bin/sdoctl 裡的預設值。
PASSWORD='換成你自己的密碼'
PORT=8888
MAX_BLOB_GB=5
UPLOAD_MB_HOUR=512
MAX_PER_IP=4
TTL_HOURS=24
# TOKENS=            # 留空 = 不驗 token(誰有密碼誰就進得來)。見 §6
# TLS_CERT=          # 留空 = 不加密。公網上別這樣
# EXTRA_ARGS=(-v)    # 印出每一筆收到的訊息;查完記得註解回去
EOF

sudo chown root:yhaniki /etc/sdo/sdoctl.conf
sudo chmod 640 /etc/sdo/sdoctl.conf
```

`640 root:yhaniki`:你讀得到(`sdoctl` 是你在跑的),這台其他使用者讀不到。

四個參數為什麼是這些值:

| 參數 | 值 | 理由 |
|---|---|---|
| `PORT` | **8888** | 這台外部通的是 22 / 27017 / 8888。填 27015 會變成「server 有在跑但誰都連不進來」 |
| `MAX_BLOB_GB` | **5** | 磁碟只剩 21G,而且是跟 palworld/L4D2 共用的同一顆。預設值 20 會在 TTL 到期前先把磁碟塞爆,連累別人的服務 |
| `UPLOAD_MB_HOUR` | 512 | 擋「拿 server 當免費網路硬碟」。四個人各傳一輪歌綽綽有餘 |
| `MAX_PER_IP` | 4 | 一份 client 正常吃 2 條(control + file),所以 4 = 同一個 IP 兩份 client |

`PASSWORD` 記得換掉:預設值 `abab123` 是寫在公開原始碼裡的,`sdoctl start` 沒改也會警告。

> ⚠️ **密碼會出現在 `ps` / `/proc` 裡**,同一台機器上任何帳號都看得到(這台有別的使用者)。
> server 沒有 `--password-file` 這個選項,所以真正的身分保證要靠 token,密碼只當進站門檻。
> 設定檔的 640 只是少一個外洩管道,不是保護。

---

## 8. 啟動並確認

```bash
sdoctl start
```

`start` 會等到 port 真的 bind 起來才回報 —— 「行程還在」不等於起來了(參數錯 / 憑證讀不到
是啟動到一半才 exit 的)。成功長這樣:

```
✓ 已啟動,port 8888 在聽

===== sdoctl start 2026-07-28 22:10:03 =====
[sdo-server] TLS 已啟用(TLS 1.2/1.3)。憑證指紋 SHA-256:
[sdo-server]   6473b40678340324aa73a7cd6144d2168cdbc24f3d3a04ef469a672cc2c92e22
```

**要看到指紋、而且沒有任何 `⚠️` 開頭的行。** 有的話 `sdoctl` 會再補一句提醒:

```
[sdo-server] ⚠️  沒有加密(明文 TCP)。要加密請給 --tls-cert <pfx>。
[sdo-server] ⚠️  沒有帳號認證 —— 身分由 client 自稱。要認證請給 --tokens <file>。
```

看到任何一行就是**還沒準備好開公網**,回去補 §5 / §6。

起不來的話 `sdoctl start` 會把這次的輸出整段印出來(exit code 對照見 [§15](#15-疑難排解))。
憑證讀不到 / 沒有私鑰 / pfx 密碼錯 → **直接開不起來(exit 4)**,不會偷偷退回明文。

---

## 9. 從外面驗證 port 真的通

在 server 上 `sdoctl status` 說「有在聽」只能證明它在聽,**證明不了外面進得來**。
8888 這條 GCP 規則沒實測過,這步不能跳。要從這台電腦測:

```powershell
Test-NetConnection -ComputerName srcds.yhaniki.com -Port 8888
```

想要快一點(不等 5 秒逾時的完整輸出):

```powershell
$c = New-Object System.Net.Sockets.TcpClient
$ar = $c.BeginConnect('srcds.yhaniki.com', 8888, $null, $null)
if ($ar.AsyncWaitHandle.WaitOne(5000)) { $c.EndConnect($ar); "REACHABLE: $($c.Connected)" } else { "BLOCKED" }
$c.Close()
```

BLOCKED 的話先看 GCP 那條規則有沒有綁 `--target-tags`(這台的 network tags 是空的,綁了就不會套用)。

---

## 10. 日常操作

```bash
sdoctl status          # tmux session / server 行程 / port 三件事
sdoctl attach          # 進 tmux 看畫面(唯讀),Ctrl-B 放開再按 D 離開
sdoctl log 100         # log 最後 100 行
sdoctl restart         # 改完 /etc/sdo/sdoctl.conf 之後
sdoctl stop
```

- `attach` **預設是唯讀的**(`tmux attach -r`)。在 tmux 裡按 Ctrl-C 就直接殺掉 server 了,
  唯讀模式下打字傳不進去。真的要操作才用 `sdoctl attach --rw`。
- 離開 tmux 是 **Ctrl-B 放開再按 D**。按 Ctrl-C 或關掉 SSH 視窗都不會停掉 server
  (那正是用 tmux 的意義),但 Ctrl-C 在 `--rw` 模式下會。
- `stop` 送 SIGTERM,server 會把連線乾淨收掉;10 秒還沒退才改送 KILL。
- log 在 `/var/lib/sdo-server/server.log`,**會一直長**。tmux 沒有 journald 幫忙輪替,
  開了 `EXTRA_ARGS=(-v)` 之後尤其長 —— 查完把 `-v` 註解回去,必要時
  `sudo truncate -s 0 /var/lib/sdo-server/server.log`。

---

## 11. 開機自動啟動與免密碼(可選)

tmux 不會自己回來。要開機自動起,加到 **root 的** crontab
(root 跑 `sudo` 免密碼,腳本裡的 `sudo -u sdo` 才會通):

```bash
sudo crontab -e
```

```
@reboot /usr/local/bin/sdoctl start
```

嫌每個操作都問 sudo 密碼的話:

```bash
sudo tee /etc/sudoers.d/sdoctl > /dev/null <<'EOF'
yhaniki ALL=(sdo)  NOPASSWD: /usr/bin/tmux, /usr/bin/test
yhaniki ALL=(root) NOPASSWD: /usr/bin/tail, /usr/bin/pkill, /usr/bin/test
EOF
sudo chmod 440 /etc/sudoers.d/sdoctl
sudo visudo -c
```

> 這不是提權:`yhaniki` 本來就有完整 sudo,這只是省掉密碼那道確認。
> 不想放寬就別建這個檔,`sdoctl` 一樣能用,只是會問密碼。

---

## 12. 更新流程(改版重傳)

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
sdoctl stop
sudo install -o root -g root -m 755 /tmp/sdo-server /opt/sdo-server/sdo-server
rm -f /tmp/sdo-server
sdoctl start
```

執行檔在跑的時候不能直接覆蓋(`Text file busy`),所以 `stop` 一定要在 `install` 之前。

### ⚠️ `dotnet publish` 不能跳過,而且重啟後要確認版本

`scp` 傳的是 **publish 的產物**。少跑 `dotnet publish` 就是把**上一次**的 binary 又傳一次上去 ——
server 看起來一切正常(它就是舊的那顆,當然正常),但新加的訊息型別對它來說不存在,
所以症狀是「新功能完全沒反應、server log 也沒有任何相關訊息」,和「功能寫壞了」長得一模一樣。
實際踩過:密語做完之後在線上完全沒反應,原因就是這個。

啟動後第一行就是版本,對一下:

```bash
sdoctl log 5
# [sdo-server] sdo-server v1.5.0-dev-50359  (protocol v1)
```

尾巴那段要與 client 視窗標題一致(`dance v1.5.0-dev-50359`)—— 那是同一個 commit 的意思。
不一致的話 server 在每個人連進來時也會自己喊:

```
user 3 「飄漂o」上線(#3) client=dance v1.5.0-dev-d41da
⚠️  版本不一致:client=dance v1.5.0-dev-d41da server=sdo-server v1.5.0-dev-50359 —— …
```

`sdoctl.sh` 自己改了的話同樣重傳一次(它不含機密,直接覆蓋不會洗掉 `/etc/sdo/sdoctl.conf`):

```bash
sudo install -o root -g root -m 755 /tmp/sdoctl /usr/local/bin/sdoctl
```

歌曲暫存要清掉的話(TTL 沒到但想手動清):

```bash
sdoctl stop
sudo rm -rf /var/lib/sdo-server/blobs/*
sdoctl start
```

---

## 13. client 端設定

每個玩家的 `<DataRoot>\PROFILE\config.ini`。DataRoot 看 repo 根的 `data_root.txt`,
**不是** exe 旁邊那份 DATA(`log.txt` 第一行會印出實際用的那個)。

```ini
[Net]
serverAddress=srcds.yhaniki.com
serverPort=8888
serverPassword=你在 §7 設的那個
serverToken=這個人專屬的那一把
serverTls=1
serverCertFingerprint=§5 印出來的那 64 個 hex
netAutoDownload=1
netMaxDownloadMb=200
```

- `serverAddress` 吃 hostname(client 走 `TcpClient.BeginConnect(host, port)`),不必填 IP。
  填 hostname 的好處是 GCP 換 IP 時不用改每個人的設定。
- `serverPort` **一定要改成 8888**,預設值是 27015 —— 忘了改的症狀是「連線逾時」,
  而那個訊息不會告訴你是 port 沒改。
- `serverTls=1` 不能少:server 開了 TLS 而 client 沒開 → 連不上,**絕不會退回明文**。
- 每個人的 `serverToken` 不一樣,`serverPassword` / `serverCertFingerprint` 全部一樣。

### 常見症狀對照

| 症狀 | 原因 |
|---|---|
| 連線逾時 | `serverPort` 忘了改 8888 / server 沒在跑(`sdoctl status`)/ GCP 那條規則沒生效 |
| 密碼不符 | `serverPassword` 與 `sdoctl.conf` 的 `PASSWORD` 不一致 |
| 伺服器不認得這個 token | `serverToken` 沒填或不在 `/etc/sdo/tokens.txt` 裡 |
| 憑證驗證失敗…自簽憑證要填 serverCertFingerprint | `serverCertFingerprint` 沒填 |
| 憑證指紋不符(設定的是 xxx…,收到的是 yyy…) | 重新產過憑證但沒更新 client。訊息帶兩邊前 16 碼,好對照 |
| TLS 握手失敗 | `serverTls` 與 server 的 `TLS_CERT` 不一致 |
| 進遊戲直接是單機版面(兩顆鈕而不是三顆) | `serverAddress` 是空的 → 連線層根本沒建起來 |

連不上時 client 會提示並**自動退回單機**,不會卡在開機畫面 —— 所以「看起來能玩」不代表連上了,
以選男女畫面是三顆鈕還是兩顆鈕為準。

---

## 14. 為什麼是 tmux 不是 systemd

systemd unit 的寫法在 [OPERATIONS.md §4.5](OPERATIONS.md#45-systemd)。這台選 tmux,
代價要知道:

| 少了什麼 | 補救 |
|---|---|
| **沒有 `Restart=on-failure`** | server 掛了就是掛了。要自動重啟只能回去用 systemd,或自己包一層 while 迴圈 |
| 沒有 journald(輪替、`journalctl -u`) | `sdoctl` 把 stdout 用 `tee` 落到 `/var/lib/sdo-server/server.log`,`sdoctl log` 讀那個檔。**沒有自動輪替**,見 §10 |
| 沒有開機自動啟動 | §11 的 `@reboot` crontab |
| 沒有 `ProtectSystem` / `PrivateTmp` 那些 sandbox | 只剩「跑在 `sdo` 這個沒權限的帳號底下」這一層。執行檔 owner 是 root 所以改不掉自己 |

換來的是:能 `attach` 進去直接看畫面、開關不用 sudo systemctl、不必動 `/etc/systemd`。

> 順帶一提,systemd unit 那份如果照抄且資料放在家目錄底下,`ProtectHome=true` 會把 `/home`
> 遮成空的 → 憑證讀不到、直接 exit 4。這也是這份手冊把東西放 `/opt` + `/var/lib` + `/etc` 的原因之一。

`sdoctl` 用 `tmux -L sdo` 開專屬 socket:不會跟你自己或 L4D2 的 tmux session 混在一起,
你的 `tmux ls` 也看不到它(要看是 `sudo -H -u sdo tmux -L sdo ls`)。

---

## 15. 疑難排解

**server 起不來**

```bash
sdoctl start          # 失敗時會把這次的輸出整段印出來
sdoctl log 50
```

| exit code | 意思 |
|---|---|
| 1 | 執行期致命錯誤 —— **port 被佔用是這個** |
| 2 | 參數錯誤(`/etc/sdo/sdoctl.conf` 打錯,stderr 會印出完整說明) |
| 3 | 建不出資料目錄(`/var/lib/sdo-server` 的 owner 不是 `sdo`?) |
| 4 | TLS 設定有問題 —— 憑證讀不到 / 沒有私鑰 / pfx 密碼錯。**它拒絕以明文啟動** |

**「`--tls-cert` 找不到憑證檔」但檔案明明就在**

`/etc/sdo` 的目錄權限。`sdo` 不在 root group,750 root:root 會讓它連目錄都穿不過去,
而 open 失敗回報成「找不到」。同一個原因也會讓 `sdoctl` 讀不到 `sdoctl.conf` ——
症狀是**密碼靜靜退回 CHANGEME**(它只會說一句警告,不會停下來)。

```bash
sudo chmod 755 /etc/sdo
sudo -u sdo test -r /etc/sdo/cert.pfx && echo "sdo 讀得到了"
```

`sdoctl start` 現在會在啟動前先驗這件事,直接指出是誰讀不到哪個檔。

**`sdoctl start` 說 tmux 開不起來**

1. 裝了嗎?`command -v tmux`
2. `sdo` 的 shell 是不是 `nologin`?`getent passwd sdo` —— 必須是 `/bin/bash`(見 §3)
3. `sdo` 的 home 存不存在、寫不寫得進去?`sudo -H -u sdo touch /var/lib/sdo-server/.t`

**外面連不進來,但 `sdoctl status` 說有在聽**

1. GCP 那條 8888 的規則是不是綁了 `--target-tags`?這台的 tags 是空的,綁了不會套用
2. `PORT` 是不是真的 8888?`sdoctl status` 印的是設定檔生效後的值
3. 機器上的 `ufw` 不用管(inactive),擋在 GCP VPC 層

**磁碟滿了**

```bash
df -h /
sudo du -sh /var/lib/sdo-server/blobs
```

這台是跟 palworld / L4D2 共用同一顆磁碟的,塞爆會連累別人。`MAX_BLOB_GB=5` 是上限不是配額,
真的吃緊就往下調,或縮 `TTL_HOURS`。log 檔也要看一眼(§10)。

**看每一筆收發的訊息**

`/etc/sdo/sdoctl.conf` 把 `EXTRA_ARGS=(-v)` 那行取消註解,`sdoctl restart`。
訊息量很大(而且會一直寫進 `server.log`),查完記得改回去。

---

## 16. 之後想換 port

改兩個地方,外加驗證:

1. `/etc/sdo/sdoctl.conf` 的 `PORT=`,然後 `sdoctl restart`
2. 每個玩家 config.ini 的 `serverPort`
3. 照 §9 從外面驗證新 port 真的通,**再**通知大家改設定

新 port 要先在 GCP Console 加規則(VM 內沒權限做這件事 —— 它的 service account 沒有 compute scope):

```bash
gcloud compute firewall-rules create sdo-server \
  --network default --direction INGRESS --priority 1000 \
  --action ALLOW --rules tcp:8888 --source-ranges 0.0.0.0/0
```

⚠️ **不要加 `--target-tags`**。那台 VM 的 network tags 是空的 `[]`,綁了 tag 規則就不會套到它。

### 另一條路:ZeroTier

那台已經在 ZeroTier 網路 `Yhaniki`(`a84ac5c10a080dad`),server 端 IP `172.22.38.132`。
玩的人都在這個網路裡的話,`serverAddress=172.22.38.132` 就通了,GCP 完全不用碰,
而且不曝露在公網。代價是每個玩家都要入網。
