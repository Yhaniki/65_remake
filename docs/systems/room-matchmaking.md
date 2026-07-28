# 房間(Room / Matchmaking)

> **這份文件在 2026-07 被整份重寫。** 舊版是草案:資料結構帶問號(`maxPlayers: 6，待確認`)、
> 「房主離開=解散」、觀戰與房主轉移列在「之後」、末尾兩個待確認問題。這些都已經有答案而且
> 做出來了(而且有幾條與舊版**相反**),所以整份換掉。

## 一句話

房號制:開房拿到一個 5 位數房號,別人輸入房號加入。六個座位 + 十個旁觀席。
房間的狀態由 server 保管,每次變更推一份完整快照給房內所有人。

## 座位與旁觀

| | 數量 | 說明 |
|---|---|---|
| 座位(舞者) | 6 | `Open` / `Closed`(被房主關掉)/ `Taken` |
| 旁觀席 | 10 | 官方就是 10 個 looker 站位(EXE `0x583af0`,`RoomLayout.SpectatorAnchors`) |

加入時取 index 最小的 `Open` 座位;沒有 → `full`。
**六個座位全空是合法狀態**,那時沒有房主(`hostUserId = 0`),而**第一個坐上座位的人自動接手**。
房間只在「一個人都不剩」時關閉 —— 旁觀者算人。

## 房主

`hostUserId` 是一個獨立的欄位,**不是「座位 0 的人」**。徽章與權限一律跟它走
(轉移房主時 server 只換這個值,不搬座位)。

- **房主離開 → 自動轉給剩下座位 index 最小的人。**
  ⚠️ 這與離線 `MockRoomService` 的「房主走 = 解散」**刻意分歧**(需求要求切換房主)。
- 沒有座位玩家了 → `hostUserId = 0`(無房主)。旁觀者不會被指派成房主:
  沒有舞者的房間本來就不需要選歌或開始,所以「無房主」不是壞狀態而是正確狀態。
- **房主沒有「準備」這個狀態。** 它那格畫 host 徽章(`master.an` = 官方的 "HOST"),不畫準備標記;
  它按的是「開始」。實作上房主的 `Ready` 一律留 `false`,判定用「是房主 或 Ready」。
  🔴 不要用「房主恆 Ready = true」—— 那會讓 `Ready` 同時承載兩種語意,每個讀它的地方都要記得排除房主,
  忘一處就錯。最直接的受害者是「房主永遠不能選自己的隊」(換隊條件是「還沒準備」),
  於是組隊模式永遠開不了場。

## 只有房主能做的事

`setSong`、`setRoomSettings`、`setRoomName`、`requestStart`、`kickUser`、`setSeatClosed`、
`transferHost`、`assignTeams`。

非房主送這些 → `error{notHost}`,**絕不靜默忽略**。
client 隱藏按鈕只是 UX,**兩層都要做**(改過的 client 送得出來)。

反過來,**速度 / note 皮 / 掉落方向是個人偏好,不同步、不 gate** —— 官方也是各自設定,
而且會寫回自己的 `config.ini`。

## 座位操作(需求 12)

| 操作 | 怎麼觸發 | 規則 |
|---|---|---|
| 踢人 | 頭貼右鍵 → 選單 | 目標非自己 |
| 關閉/開啟座位 | 頭貼右鍵 → 選單;或**雙擊**空位 | 關自己那格 → `error{badSeat}` |
| 鎖住有人的格子 | **雙擊**有人的頭貼 | 先 `kicked{seatClosed}` 把人踢掉,再標 `Closed` |
| 切換房主 | 頭貼右鍵 → 選單 | |

## 組隊

- 房主一鍵分隊(`assignTeams`):**2v2 / 3v3 / 2v2v2**,server 依座位順序平均分配並驗人數相符。
- 玩家可以自己換隊(`setOwnTeam`),但**按下準備之後就不能改**(`playState` 必須是 `idle`)。
- 🔴 **組隊模式下湊不出合法 layout 就不准開始**(連 `force` 也一樣擋)。
  理由:`TeamFormationCatalog` 只有那三張逐字重製自 EXE 的座標表,沒有 3v2、4v1、5 人的站位資料。
  **擋住而不是退回個人隊形** —— 退回會讓玩家以為分隊生效了卻看到單人站位,那是靜默的錯誤行為。
  房間端會先本地預檢(灰掉「開始」鈕 + 提示需要 2v2/3v3/2v2v2),server 仍獨立驗一次。
- 參與者集合是「ready 且有歌」的座位,**不是全部座位玩家** ——
  6 人房只有 4 人 ready 且 A/B 各 2 人 → 2v2 合法,可以開始。

## 開場(兩段式,照抄 osu)

```
open ──requestStart──> waitingForLoad ──沒人還在載──> playing ──沒人還在打──> results ──> open
```

- **參與者集合在 `requestStart` 那一刻凍結** = 「(是房主 或 已準備)且 `avail == have`」的座位。
  非參與者維持 `idle` **留在房間**,看得到其他人的頭貼變 PLAYING 徽章。
- `loaded` = 程式載完;`readyForGameplay` = 人準備好。推進條件是「沒人還在 `waitingForLoad`」——
  所以 `readyForGameplay` **不阻塞開場**。
- **載入逾時 30 秒**:還在 `waitingForLoad` 的人逐出本場;卡在 `loaded` 的**強制轉 `playing`**。
  一個人卡住不會讓全房卡死。
- **雙擊「開始」= 強制開始**:第一次按顯示「再按一次強制開始」,1.5 秒內第二次 → `force:true`。
  沒歌/沒準備的人留在房間。
- `waitingForLoad`/`playing`/`results` 是 **server 保留狀態**,client 送不進來。
- 房間在 `waitingForLoad`/`playing` 期間**任何人都按不了準備**。

## 旁觀

房內按「旁觀」鈕交出座位;再按一次搶回座位。三道門:

| 情況 | 結果 |
|---|---|
| 已按準備的一般玩家 | `badState` —— 先取消準備 |
| 已經在這一場裡(`playState != idle`) | `badState` —— 不能中途離場 |
| 房主 | 先把 `hostUserId` 交給剩下座位 index 最小的人;沒人能接手 → `badState` |

已經開打的房間**可以加入來旁觀**(`spectate` 不受 `inGame` 限制),但**不會中途插進 gameplay** ——
留在房間看頭貼狀態。旁觀者缺歌**不自動下載**;有歌才跟著進場看別人跳舞。
遊戲中 Ctrl+Q 直接離開房間回選角色畫面。

## 斷線

socket 關閉或 15 秒沒收到 ping == `leaveRoom`(idempotent)。
遊玩中斷線 → 逐出本場,但**最後一筆 frame 仍列入結算**(標 `disconnected`)。
房主遊玩中斷線 → **本場繼續**(分數是 client 權威),房主照上面的規則轉移。

## 同步

每次變更推整份 `roomState`(含 `rev`)給房內所有人,**不推 delta、不做樂觀更新**。
理由見 [networking.md](networking.md)。房間列表 `roomList` 是拉的(進大廳/按重整才問)。

## 離線

`config.ini` 的 `[Net] serverAddress` 留空 → 走 `MockRoomService`,行為與加連線之前一字不差。
離線的房號也是 5 位數(共用 `RoomCodePool`),座位/準備/聊天都是本機模擬。

## 相關

- [net-protocol.md](net-protocol.md) —— 訊息集合與 R1..R21 每一條規則
- [net-song-transfer.md](net-song-transfer.md) —— 缺歌自動傳檔
- [networking.md](networking.md) —— 傳輸層與診斷
- [../screens/04-room/spec.md](../screens/04-room/spec.md) —— 房間畫面的版位
