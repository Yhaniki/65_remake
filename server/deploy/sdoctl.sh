#!/usr/bin/env bash
# sdoctl —— 用 tmux 跑 sdo-server(以獨立的 sdo 帳號)
#
#   sdoctl start | stop | restart | attach [--rw] | status | log [N]
#
# 這支腳本本身不含任何機密:密碼之類的放 /etc/sdo/sdoctl.conf(640 root:yhaniki),
# 所以改版時可以直接覆蓋這支,設定不會被洗掉。
# 安裝與完整部署步驟見 server/DEPLOY.md。
set -uo pipefail
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

CONF=/etc/sdo/sdoctl.conf

# ── 預設值 ──────────────────────────────────────────────────────────
# 要改請改 $CONF,不要改這裡 —— 這支腳本更新時會被整份覆蓋。
SDO_USER=sdo
SESSION=sdo
SOCKET=sdo                 # tmux -L:專屬 socket,跟這台其他人的 tmux 完全分開
BIN=/opt/sdo-server/sdo-server
DATA=/var/lib/sdo-server
PORT=8888
PASSWORD=CHANGEME
TLS_CERT=/etc/sdo/cert.pfx
TOKENS=/etc/sdo/tokens.txt
MAX_PER_IP=4
UPLOAD_MB_HOUR=512
MAX_BLOB_GB=5
TTL_HOURS=24
EXTRA_ARGS=()              # 例:EXTRA_ARGS=(-v) 印出每一筆收到的訊息(量很大)

# shellcheck source=/dev/null
[ -r "$CONF" ] && . "$CONF"

LOG=$DATA/server.log

ARGS=(
  --port "$PORT"
  --data "$DATA"
  --password "$PASSWORD"
  --max-per-ip "$MAX_PER_IP"
  --upload-mb-hour "$UPLOAD_MB_HOUR"
  --max-blob-gb "$MAX_BLOB_GB"
  --ttl-hours "$TTL_HOURS"
)

# 留空 = 那道防線不啟用。`TOKENS=` 就是「不驗 token,誰有密碼誰就進得來,
# 名字與 playerId 由 client 自稱」。
# 不能改成一律帶 `--tokens ""`:server 會去讀一個檔名是空字串的檔,讀不到就**靜靜跳過認證** ——
# 結果一樣,但你會以為認證還開著。
if [ -n "$TLS_CERT" ]; then ARGS+=(--tls-cert "$TLS_CERT"); fi
if [ -n "$TOKENS" ];   then ARGS+=(--tokens "$TOKENS");     fi
ARGS+=("${EXTRA_ARGS[@]}")

# ── 小工具 ──────────────────────────────────────────────────────────

say() { printf '%s\n' "$*"; }

# 所有 tmux 操作都以 sdo 身分執行。-H 是必要的:tmux 需要一個寫得進去的 HOME,
# 而 sudo -u 預設不會把 HOME 換掉(會留著 /home/yhaniki,sdo 沒權限)。
tm()        { sudo -H -u "$SDO_USER" tmux -L "$SOCKET" "$@"; }
running()   { tm has-session -t "$SESSION" 2>/dev/null; }
alive()     { pgrep -u "$SDO_USER" -f "$BIN" >/dev/null 2>&1; }
listening() { ss -tln 2>/dev/null | grep -q ":$PORT "; }

# 這次開機那一段(從 log 裡最後一條 sdoctl 分隔線起算)
banner() {
  sudo tail -n 200 "$LOG" 2>/dev/null |
    awk '/^===== sdoctl start /{buf=""} {buf = buf $0 "\n"} END{printf "%s", buf}'
}

# 由 tmux server(已經是 sdo 身分)去送訊號,不必拿 root 殺 process;
# tmux 已經不在了就退回 sudo pkill 收殘留的孤兒行程。
kill_server() {
  tm run-shell "pkill -$1 -u $SDO_USER -f $BIN" >/dev/null 2>&1 ||
    sudo pkill "-$1" -u "$SDO_USER" -f "$BIN" >/dev/null 2>&1
}

# ── 子命令 ──────────────────────────────────────────────────────────

status() {
  say "sdo-server @ $BIN"
  running   && say "  tmux session : 在($SOCKET/$SESSION)" || say "  tmux session : 不在"
  alive     && say "  server 行程  : 在"                   || say "  server 行程  : 不在"
  listening && say "  port $PORT   : 有在聽"               || say "  port $PORT   : 沒在聽"
  say "  log          : $LOG"
  say "  log 檔       : $DATA/logs/(server 自己依日期分,總量 100 MB 滿了從最舊的刪)"
}

logs() { sudo tail -n "${1:-50}" "$LOG" 2>/dev/null || say "還沒有 log:$LOG"; }

# 啟動前先確認「該讀得到的人真的讀得到」。server 對讀不到的憑證只會說
# 「找不到憑證檔」,看起來像檔案沒產出來,實際上多半是目錄權限。
preflight() {
  local fatal=0 f
  if sudo test -e "$CONF" 2>/dev/null && [ ! -r "$CONF" ]; then
    say "⚠️  $CONF 存在但這個帳號讀不到 → 現在用的是內建預設值,不是你設的密碼/port"
    say "    修:sudo chmod 755 $(dirname "$CONF")"
  fi
  for f in "$TLS_CERT" "$TOKENS"; do
    [ -n "$f" ] || continue
    if ! sudo -u "$SDO_USER" test -r "$f" 2>/dev/null; then
      say "✗ $SDO_USER 讀不到 $f"
      say "    多半是目錄權限:sudo chmod 755 $(dirname "$f")"
      fatal=1
    fi
  done
  return $fatal
}

start() {
  if alive; then say "已經在跑了(sdoctl attach 看畫面)"; return 0; fi
  [ -x "$BIN" ] || { say "✗ 找不到執行檔或沒有執行權限:$BIN"; return 1; }
  preflight || return 1
  [ "$PASSWORD" = CHANGEME ] && say "⚠️  --password 還是 CHANGEME —— 去改 $CONF"
  running && tm kill-session -t "$SESSION" >/dev/null 2>&1   # 清掉上次留下的死 pane

  # server 現在自己把每一行(含時間戳)寫進 $DATA/logs/,依日期分檔、滿 100 MB 從最舊的刪。
  # 底下 tee 的這一份只剩一個用途:接住「log 檔開起來之前」就死掉的訊息(參數錯、data 目錄
  # 建不出來、TLS 憑證讀不到)。它沒有輪替,所以長到一定程度就從頭來過 ——
  # 不然它會是這台機器上唯一一個會無限長的東西,而完整記錄本來就在 logs/ 裡。
  if [ "$(sudo stat -c %s "$LOG" 2>/dev/null || echo 0)" -gt $((10 * 1024 * 1024)) ]; then
    say "$LOG 超過 10 MB,清空重來(完整記錄在 $DATA/logs/)"
    sudo truncate -s 0 "$LOG" 2>/dev/null || true
  fi

  local cmd qlog
  cmd=$(printf '%q ' "$BIN" "${ARGS[@]}")
  qlog=$(printf '%q' "$LOG")

  # tee:tmux 沒有 journal,server 秒掛時畫面會消失,log 檔才留得住。
  # 前面那條 date 分隔線是給 banner() 用的。
  tm new-session -d -s "$SESSION" -c "$DATA" \
     "date '+===== sdoctl start %F %T =====' >> $qlog; $cmd 2>&1 | tee -a $qlog" ||
    { say "✗ tmux 開不起來"; return 1; }
  tm set-option -t "$SESSION" remain-on-exit on >/dev/null 2>&1

  # 等到真的 bind 起來為止 —— 「行程還在」不等於起來了(self-contained 首次啟動要 1~3 秒,
  # 而參數錯 / 憑證讀不到是啟動到一半才 exit 的)。
  local i
  for i in $(seq 20); do
    listening && break
    alive || break
    sleep 1
  done

  local out
  out=$(banner)
  if listening; then
    say "✓ 已啟動,port $PORT 在聽"
    echo
    printf '%s\n' "$out"
    if printf '%s' "$out" | grep -q '⚠'; then
      echo
      say "⚠️  上面每一行 ⚠️ 就是一道沒生效的防線 —— 別就這樣開公網。"
    fi
  else
    say "✗ 沒起來。這次的輸出:"
    echo
    printf '%s\n' "$out"
    return 1
  fi
}

stop() {
  if ! alive && ! running; then say "沒在跑"; return 0; fi
  kill_server TERM
  local i
  for i in $(seq 10); do alive || break; sleep 1; done
  if alive; then
    say "10 秒還沒退,改送 KILL"
    kill_server KILL
    sleep 1
  fi
  tm kill-session -t "$SESSION" >/dev/null 2>&1
  say "✓ 已停止"
}

attach() {
  running || { say "沒在跑,先 sdoctl start"; return 1; }
  if [ "${1:-}" = --rw ]; then
    say "→ 可寫模式。離開:Ctrl-B 放開再按 D。⚠️ 按 Ctrl-C 會殺掉 server。"
    tm attach-session -t "$SESSION"
  else
    say "→ 唯讀模式(打字不會傳進去)。離開:Ctrl-B 放開再按 D。"
    tm attach-session -t "$SESSION" -r
  fi
}

usage() {
  cat <<'USAGE'
用法: sdoctl <子命令>

  start            用 tmux 開起來(已經在跑就不動),等它真的 bind 起來才回報
  stop             送 SIGTERM,最多等 10 秒,再收掉 tmux session
  restart          stop + start(改完 /etc/sdo/sdoctl.conf 用這個)
  attach [--rw]    進 tmux 看畫面。預設唯讀;離開按 Ctrl-B 放開再按 D
  status           tmux session / server 行程 / port 三件事
  log [N]          server log 最後 N 行(預設 50)

設定檔:/etc/sdo/sdoctl.conf(不存在就用腳本內的預設值)
USAGE
}

case "${1:-}" in
  start)     start ;;
  stop)      stop ;;
  restart)   stop; start ;;
  attach)    shift; attach "${1:-}" ;;
  status)    status ;;
  log)       shift; logs "${1:-50}" ;;
  -h|--help) usage ;;
  '')        usage; exit 2 ;;
  *)         say "不認得的子命令:$1"; echo; usage; exit 2 ;;
esac
