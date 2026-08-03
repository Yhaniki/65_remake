#!/usr/bin/env bash
# bootstrap.sh —— 在 server 上一次把 sdo-server 裝好(tmux + 獨立的 sdo 帳號)
#
# 會做這些:裝 tmux、建 sdo 帳號與目錄、裝執行檔與 sdoctl、產自簽憑證、產 token、
#           寫 /etc/sdo/sdoctl.conf、啟動、最後印出每個玩家要填的 config.ini。
#
# sudo 密碼只在開頭問一次。**可以重複跑** —— 已經存在的憑證 / token / 設定檔一律不動
# (那三個一換掉,所有玩家的 config.ini 就得跟著改;要重產請自己先刪掉那個檔)。
#
# 用法:
#   ./bootstrap.sh                                  # 全部用預設,互動問密碼
#   ./bootstrap.sh --players 4                      # 產 4 把 token
#   ./bootstrap.sh --port 8888 --host srcds.yhaniki.com
#   ./bootstrap.sh --bin /tmp/sdo-server --sdoctl /tmp/sdoctl
#
# 完整說明見 server/DEPLOY.md。
set -euo pipefail

HOST=srcds.yhaniki.com
PORT=8888
PLAYERS=2
BIN_SRC=
SDOCTL_SRC=
PASSWORD=

SDO_USER=sdo
DATA=/var/lib/sdo-server
OPT=/opt/sdo-server
ETC=/etc/sdo

SELF_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

die()  { printf '\n✗ %s\n' "$*" >&2; exit 1; }
step() { printf '\n── %s\n' "$*"; }
info() { printf '   %s\n' "$*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --host)     HOST=${2:?}; shift 2 ;;
    --port)     PORT=${2:?}; shift 2 ;;
    --players)  PLAYERS=${2:?}; shift 2 ;;
    --bin)      BIN_SRC=${2:?}; shift 2 ;;
    --sdoctl)   SDOCTL_SRC=${2:?}; shift 2 ;;
    --password) PASSWORD=${2:?}; shift 2 ;;
    -h|--help)  sed -n '2,20p' "$0"; exit 0 ;;
    *)          die "不認得的參數:$1(--help 看用法)" ;;
  esac
done

case "$PLAYERS" in ''|*[!0-9]*) die "--players 要是數字" ;; esac
[ "$PLAYERS" -ge 1 ] || die "--players 至少 1"
case "$PORT" in ''|*[!0-9]*) die "--port 要是數字" ;; esac

# ── 找來源檔 ────────────────────────────────────────────────────────
if [ -z "$BIN_SRC" ]; then
  for f in /tmp/sdo-server "$HOME/dance_server_data/sdo-server" "$SELF_DIR/sdo-server" ./sdo-server; do
    if [ -f "$f" ]; then BIN_SRC=$f; break; fi
  done
fi
[ -n "$BIN_SRC" ] || die "找不到 sdo-server 執行檔。先 scp 上來,或用 --bin 指定路徑。"
[ -f "$BIN_SRC" ] || die "--bin 指的檔案不存在:$BIN_SRC"

if [ -z "$SDOCTL_SRC" ]; then
  for f in /tmp/sdoctl "$SELF_DIR/sdoctl.sh" ./sdoctl.sh; do
    if [ -f "$f" ]; then SDOCTL_SRC=$f; break; fi
  done
fi
[ -n "$SDOCTL_SRC" ] || die "找不到 sdoctl.sh。先 scp 上來,或用 --sdoctl 指定路徑。"
[ -f "$SDOCTL_SRC" ] || die "--sdoctl 指的檔案不存在:$SDOCTL_SRC"

printf '將要安裝:\n'
info "執行檔   : $BIN_SRC  →  $OPT/sdo-server"
info "sdoctl   : $SDOCTL_SRC  →  /usr/local/bin/sdoctl"
info "跑的帳號 : $SDO_USER(系統帳號,登不進來)"
info "資料目錄 : $DATA"
info "port     : $PORT"
info "憑證 CN  : $HOST"

step "先取得 sudo 權限(整支腳本只問這一次)"
sudo -v || die "sudo 失敗"

# ── 1. tmux ─────────────────────────────────────────────────────────
step "1/8 tmux"
if command -v tmux >/dev/null 2>&1; then
  info "已經有了:$(tmux -V)"
else
  sudo apt-get update -qq
  sudo apt-get install -y -qq tmux
  command -v tmux >/dev/null 2>&1 || die "apt 跑完了但還是沒有 tmux"
  info "裝好了:$(tmux -V)"
fi

# ── 2. 帳號與目錄 ───────────────────────────────────────────────────
step "2/8 帳號與目錄"
if id -u "$SDO_USER" >/dev/null 2>&1; then
  info "帳號 $SDO_USER 已存在"
else
  # shell 不能是 nologin:tmux 是拿 passwd 裡的 shell 去跑命令的。
  # 這個帳號沒有密碼也沒有 SSH key,登不進來。
  sudo useradd --system --create-home --home-dir "$DATA" --shell /bin/bash "$SDO_USER"
  info "建好帳號 $SDO_USER(home = $DATA)"
fi
sudo mkdir -p "$OPT" "$ETC" "$DATA"
sudo chown "$SDO_USER:$SDO_USER" "$DATA"
sudo chmod 750 "$DATA"
# $ETC 要 755:目錄權限管的是「誰穿得過去」,而 sdo(讀憑證/token)與你(讀 sdoctl.conf)
# 都不在 root group。設 750 root:root 的話兩邊都進不去,症狀是 server 說「找不到憑證檔」。
# 內容的保護在檔案本身:cert.pfx / tokens.txt 是 600 sdo,sdoctl.conf 是 640。
sudo chmod 755 "$ETC"
info "$OPT / $ETC / $DATA 就緒"

# ── 3. 執行檔與 sdoctl ──────────────────────────────────────────────
step "3/8 執行檔與 sdoctl"
if [ -x /usr/local/bin/sdoctl ]; then
  /usr/local/bin/sdoctl stop >/dev/null 2>&1 || true   # 執行中的檔案不能覆蓋(Text file busy)
fi
sudo install -o root -g root -m 755 "$BIN_SRC" "$OPT/sdo-server"

# CRLF 換行的 .sh 在 Linux 上會變成 `bad interpreter: /usr/bin/env bash^M`。
# 與其偵測到再叫人手動修,直接洗掉 —— 從 Windows 傳上來很容易踩到。
t=$(mktemp)
tr -d '\r' < "$SDOCTL_SRC" > "$t"
cmp -s "$t" "$SDOCTL_SRC" || info "sdoctl.sh 原本是 CRLF 換行,已轉成 LF"
sudo install -o root -g root -m 755 "$t" /usr/local/bin/sdoctl
rm -f "$t"
# owner 是 root、sdo 只有讀+執行 —— server 被打穿也改不掉自己的執行檔
"$OPT/sdo-server" --help >/dev/null 2>&1 || die "$OPT/sdo-server 跑不起來(publish 時忘了 --self-contained?)"
info "執行檔 OK($OPT/sdo-server --help 有反應)"

# ── 4. TLS 憑證 ─────────────────────────────────────────────────────
step "4/8 TLS 憑證"
if sudo test -f "$ETC/cert.pfx"; then
  info "已經有 $ETC/cert.pfx,不重產"
  info "(重產會讓所有玩家的 serverCertFingerprint 失效 —— 真要換就先 sudo rm 掉它)"
else
  d=$(mktemp -d)
  # openssl 的進度輸出走 stderr,所以收進 log 裡 —— 但**只有失敗時才丟掉**,
  # 不然憑證產不出來會變成「停在這一步,什麼都不說」。
  if ! openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
         -keyout "$d/key.pem" -out "$d/cert.pem" \
         -subj "/CN=$HOST" -addext "subjectAltName=DNS:$HOST" > "$d/log" 2>&1; then
    cat "$d/log" >&2; rm -rf "$d"; die "產憑證失敗(上面是 openssl 的訊息)"
  fi
  if ! openssl pkcs12 -export -out "$d/cert.pfx" \
         -inkey "$d/key.pem" -in "$d/cert.pem" -passout pass: > "$d/log" 2>&1; then
    cat "$d/log" >&2; rm -rf "$d"; die "合成 pfx 失敗(上面是 openssl 的訊息)"
  fi
  sudo install -o "$SDO_USER" -g "$SDO_USER" -m 600 "$d/cert.pfx" "$ETC/cert.pfx"
  rm -rf "$d"        # 私鑰不留在 /tmp
  info "產好了(自簽,十年份,CN=$HOST)"
fi

FINGERPRINT=$(sudo openssl pkcs12 -in "$ETC/cert.pfx" -clcerts -nokeys -passin pass: 2>/dev/null |
              openssl x509 -noout -fingerprint -sha256 |
              sed 's/.*=//; s/://g' | tr 'A-Z' 'a-z') ||
  die "讀不出 $ETC/cert.pfx —— 手動跑一次看訊息:sudo openssl pkcs12 -in $ETC/cert.pfx -clcerts -nokeys -passin pass:"
[ ${#FINGERPRINT} -eq 64 ] || die "指紋格式不對(拿到的是 '$FINGERPRINT')"
info "指紋:$FINGERPRINT"

# ── 5. token ────────────────────────────────────────────────────────
step "5/8 token"
if sudo test -f "$ETC/tokens.txt"; then
  info "已經有 $ETC/tokens.txt,不重產(重產 = 所有人的 serverToken 都要換)"
else
  t=$(mktemp)
  {
    echo "# 一行一個。格式:<token> = <playerId>, <顯示名稱>, <admin>"
    echo "# playerId 是 8 位數字,對應 DATA/PROFILE/<id>/"
    i=0
    while [ "$i" -lt "$PLAYERS" ]; do
      tok=$(openssl rand -hex 16)
      pid=$(printf '%08d' "$i")
      if [ "$i" -eq 0 ]; then
        echo "$tok = $pid, 房主, admin"
      else
        echo "$tok = $pid, 玩家$((i + 1))"
      fi
      i=$((i + 1))
    done
  } > "$t"
  sudo install -o "$SDO_USER" -g "$SDO_USER" -m 600 "$t" "$ETC/tokens.txt"
  rm -f "$t"
  info "產了 $PLAYERS 把(第一把是 admin)"
fi

# ── 6. 設定檔 ───────────────────────────────────────────────────────
step "6/8 設定檔"
MY_GROUP=$(id -gn)
if sudo test -f "$ETC/sdoctl.conf"; then
  info "已經有 $ETC/sdoctl.conf,保留原本的設定(含密碼)"
  info "要改:sudo nano $ETC/sdoctl.conf,然後 sdoctl restart"
else
  if [ -z "$PASSWORD" ]; then
    [ -t 0 ] || die "沒有 TTY 可以問密碼 —— 用 --password 帶進來"
    while [ -z "$PASSWORD" ]; do
      p1=; p2=
      read -r -s -p "   設一個進站密碼(玩家的 config.ini 要填同一個): " p1; echo
      read -r -s -p "   再打一次: " p2; echo
      if [ -z "$p1" ];        then info "不能是空的"; continue; fi
      if [ "$p1" != "$p2" ];  then info "兩次不一樣,重來"; continue; fi
      PASSWORD=$p1
    done
  fi
  t=$(mktemp)
  {
    echo "# sdoctl 的設定。沒寫到的都用 /usr/local/bin/sdoctl 裡的預設值。"
    printf 'PASSWORD=%q\n' "$PASSWORD"
    echo "PORT=$PORT"
    echo "MAX_BLOB_GB=5        # 磁碟只剩 21G,而且跟 palworld/L4D2 共用同一顆"
    echo "UPLOAD_MB_HOUR=512"
    echo "MAX_PER_IP=4         # 一份 client 吃 2 條連線(control + file)"
    echo "TTL_HOURS=24"
    echo "# EXTRA_ARGS=(-v)    # 印出每一筆收到的訊息;查完記得註解回去"
  } > "$t"
  sudo install -o root -g "$MY_GROUP" -m 640 "$t" "$ETC/sdoctl.conf"
  rm -f "$t"
  info "寫好了(640 root:$MY_GROUP —— 你讀得到,這台其他使用者讀不到)"
fi

# ── 7. 啟動 ─────────────────────────────────────────────────────────
step "7/8 啟動"
sdoctl start || die "起不來 —— 上面那段就是原因。修好之後再跑一次這支腳本(已裝好的東西不會重做)。"

# ── 8. 玩家設定小抄 ─────────────────────────────────────────────────
step "8/8 玩家設定"

CHEAT=$(mktemp)
{
  echo "# sdo-server client 設定 —— 產生自 bootstrap.sh"
  echo "# 每個玩家把對應的一段貼進自己的 <DataRoot>\\PROFILE\\config.ini"
  echo "# serverPassword 見 $ETC/sdoctl.conf 的 PASSWORD"
  echo
  # awk 而不是兩層 grep:grep 沒 match 到東西時回 1,在 pipefail 下會把整支腳本帶走
  # (token 檔剛好只剩註解時就會遇到)。
  sudo awk 'NF && $0 !~ /^[[:space:]]*#/' "$ETC/tokens.txt" | while IFS= read -r line; do
    case "$line" in
      *=*)
        tok=$(printf '%s' "${line%%=*}" | tr -d '[:space:]')
        rest=${line#*=}
        nm=$(printf '%s' "$rest" | cut -d, -f2 | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')
        [ -n "$nm" ] || nm="(沒寫名字)"
        ;;
      *)
        tok=$(printf '%s' "$line" | tr -d '[:space:]')
        nm="(這把沒綁 playerId —— 身分仍由 client 自稱)"
        ;;
    esac
    echo "### $nm"
    echo "[Net]"
    echo "serverAddress=$HOST"
    echo "serverPort=$PORT"
    echo "serverPassword=<見 $ETC/sdoctl.conf>"
    echo "serverToken=$tok"
    echo "serverTls=1"
    echo "serverCertFingerprint=$FINGERPRINT"
    echo "netAutoDownload=1"
    echo "netMaxDownloadMb=200"
    echo
  done
} > "$CHEAT"

sudo install -o root -g "$MY_GROUP" -m 640 "$CHEAT" "$ETC/client-setup.txt"
cat "$CHEAT"
rm -f "$CHEAT"

cat <<EOF
──────────────────────────────────────────────────────────────────────
這份也存在 $ETC/client-setup.txt(之後 sudo cat 就看得到)。
密碼:sudo grep PASSWORD $ETC/sdoctl.conf

還沒做完的兩件事:

1) 從**外面**驗證 port $PORT 真的通 —— server 在聽 ≠ 外面進得來。
   在你的 Windows 上跑:
     Test-NetConnection -ComputerName $HOST -Port $PORT

2) 開機自動啟動(tmux 不會自己回來):
     sudo crontab -e     然後加一行:  @reboot /usr/local/bin/sdoctl start

日常:sdoctl status / attach / log 100 / restart / stop
EOF
