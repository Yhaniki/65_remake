# 線上實機驗證

* build: `H:\65_remake-online\Build\Windows\dance.exe`  (07/28/2026 18:39:46)
* server: `--port 27099 --bind 127.0.0.1 --data H:\65_remake-online\.verify_online\srvdata --password abab123 -v --tls-cert H:\65_remake-online\.verify_online\test.pfx --tokens H:\65_remake-online\.verify_online\tokens.txt`
* TLS: 開(f41117ac4321dbc9c7fa48f88de770dc727f4c928bcda3656f4d2dbaf0c1b62b) / token: 開

| 檢查 | 結果 | 證據 |
|---|---|---|
| 兩台都連上 server | OK |  |
| TLS:每一條連線都是加密的 | OK | 加密連線 3 條 |
| token:啟用且沒有一條被拒 | OK |  |
| token:身分由 server 覆寫 | OK |  |
| 開房 | OK | user 1 開了房 43822 |
| 第二台加入同一間房 | OK | user 2 加入房 43822 座位 1 |
| 房主換歌廣播出去 | OK | 房 43822 換歌:Identic Conflict |
| 同步進場:server 開場 | OK | 房 43822:第 1 場開始 |
| 兩台拿到同一份 resolved(場景/隊形一致) | OK | A: [net] resolved match=1 scene=4 formation=0 teamLayout=-1 randomSong=- dancers=2 spectator=False / B: [net] resolved match=1 scene=4 formation=0 teamLayout=-1 randomSong=- dancers=2 spectator=False |
| 同場多舞者(兩台都生出別人) | OK | A: [dancers] 生出 1 位額外舞者(總共 2 位,隊形 0) / B: [dancers] 生出 1 位額外舞者(總共 2 位,隊形 0) |
| 分數流:server 收到 frame 並彙整 | OK |  |
| 結算 | OK | 房 43822:第 1 場結算 |
| A 沒有例外 | OK |  |
| B 沒有例外 | OK |  |

log:`H:\65_remake-online\.verify_online\server.log` / `H:\65_remake-online\.verify_online\clientA.log` / `H:\65_remake-online\.verify_online\clientB.log`
