# Room Chat Actions

這份文件只列官方反編譯與 Ghidra dump 找到的 room chat action 表。舊版猜測的
`laugh/bye/874/hit/cheer`、`WREST/MREST` MOT 檔名已移除。

## Evidence

### CN room bubble notes

- Source: `H:\sdo_cn\sdo.bin.c`.
- Live-client capture should use `H:\65_remake\assets\閉撰敃氪\run_hook_room_bubble.bat`. It attaches to the real `sdo.bin` process and writes `H:\65_remake\room_bubble_frida_log.jsonl`. The hook records `FUN_006f6a80` anchors, `FUN_0044abd0` cursor reads with caller return addresses, `FUN_0046ca90` / `FUN_0044f900` mouse offset changes, `FUN_004716d0` / `FUN_00471750` UI `+0x84/+0x88` position writes, `FUN_00965a20` bubble SE, and Bubble2 resource loads when attached early.
- Room movement updates `param_1 + 0xe30/e34/e38` in `FUN_006f8520` (`sdo.bin.c:434665`) and calls `FUN_006f6a80` after every coordinate change.
- `FUN_006f6a80` (`sdo.bin.c:433981`) writes room UI anchor coordinates after movement. It has more than one UI anchor write, so it is not enough evidence to force Bubble2 onto the stable `(x, y + 50, z)` point. The captured official bubble follows the avatar's motion and starts around the chest, so the remake projects an animated chest point between `Bip01_Spine1` and `Bip01_Neck` before placing Bubble2.
- `FUN_0046ca90` (`sdo.bin.c:88868`) and the similar `FUN_0044f900` (`sdo.bin.c:71385`) are UI mouse-message handlers. Message `0x20` accumulates a temporary offset with a `0.4` multiplier (`sdo.bin.c:88879-88885`, `sdo.bin.c:71396-71406`), `0x21` clears the stored pointer delta, and `0x22/0x23` apply/clear a temporary scale-like delta. The vertical clamp is `-30..15`. However the 2026-07-08 live Bubble2 capture had zero `mouse_ui` hits, so these functions are official UI-drag evidence but are not confirmed as the Bubble2 drag entry.
- Bubble2 assets are registered by global constructors: `FUN_00aa2eb0` loads `N1.an..N11.an`, `FUN_00aa2ff0`/`FUN_00aa3130` load `1.an..11.an`, `FUN_00aa3270` loads `1.msk..11.msk`, and `FUN_00aa33b0` loads `T1.an..T11.an` (`sdo.bin.c:911366-911559`).
- Room chat mode callbacks are `FUN_006f8220` and `FUN_006f69d0` (`sdo.bin.c:433941`, `sdo.bin.c:434560`), with UI names `chatmode`, `friendchatmode`, `Familychatmode`, and `chatscroll` in the room UI build block.
- CN SE registration starts at `FUN_00965620` (`sdo.bin.c:645212`) and includes `SE\bubble1.wav` at `sdo.bin.c:645222`; the remake asset set currently maps the room bubble pop to the extracted `Bubble.wav`.
- Live capture `H:\65_remake\room_bubble_frida_log.jsonl` from 2026-07-08 23:42 recorded `api_hook_ok:4`, `cursor_func:1330`, `api_get_cursor_pos:1968`, `api_screen_to_client:1371`, `asset_load:63`, `se:3`, and `mouse_ui:0`. This confirms `FUN_0044abd0` reads client cursor coordinates through the room window handle at object offset `0xb0`, while the direct WinAPI hooks also capture left-button state.
- In that capture `cursor_func` had `leftDown=1` for 1102 samples. The likely Bubble2 drag windows were `7603..11959ms` (`x=185..520`, `y=183..521`), `13645..14104ms` (`x=489..492`, `y=337..485`), and `15343..16015ms` (`x=466..470`, `y=349..536`). A later long drag (`32472..43546ms`) happened after the Bubble2 trace window and may include unrelated room/UI dragging.
- The same capture shows Bubble2 spawn groups loading `Bubble2\N1.an` and `Bubble2\empty.an`, with `SE\Bubble.wav` (`idx=236`) played for each bubble pop. No sent-bubble transform was logged yet, only cursor coordinates and asset/SE activity.
- After that capture, `hook_room_bubble_online.js` was strengthened to emit `cursor_func.ret` and gated `ui_pos` rows for `FUN_004716d0` / `FUN_00471750`. If another capture is needed, those rows should identify which UI object receives the bubble position writes during spawn, drag, and release.
- Live capture from 2026-07-08 23:51 recorded `hook_ok:9`, `api_hook_ok:4`, `cursor_func:937`, `api_get_cursor_pos:1362`, `api_screen_to_client:952`, `asset_load:9`, `se:3`, with `mouse_ui:0` and `ui_pos:0`. The three clean left-drag windows were `7389..10835ms` (`x=233..532`, `y=209..512`), `12490..13144ms` (`x=468..475`, `y=312..502`), and `14423..15218ms` (`x=451..478`, `y=246..503`).
- In the 23:51 capture every `cursor_func.ret` was `0x42991a`, which is the return site after `FUN_0044abd0` inside `FUN_004298b0` (`sdo.bin.c:42272-42352`). That function clamps the client cursor, packs `y << 16 | x`, and dispatches event `7` through the UI event system.
- Decompile follow-up: `FUN_00476a90` converts event `7` to `FUN_0045ef60`, while `FUN_00465740` turns held mouse state into UI mouse messages `0x20` drag, `0x21` drag_end, `0x22` scale_drag, and `0x23` scale_end (`sdo.bin.c:83390-83530`). `FUN_0045ef60` then fans those messages out to registered listener objects (`sdo.bin.c:78897-78911`).
- After the 23:51 capture, `hook_room_bubble_online.js` was strengthened again to hook `FUN_0045ef60`, emit `mouse_dispatch` listener rows, and dynamically attach each listener's vtable slot 0 as `mouse_target`. This should expose the concrete Bubble2 drag handler address and before/after object fields during the next capture.
- Live capture from 2026-07-08 23:57 recorded `hook_ok:10`, `api_hook_ok:4`, `cursor_func:805`, `api_get_cursor_pos:1102`, `api_screen_to_client:809`, `asset_load:12`, `se:4`, but still no `mouse_dispatch`, `mouse_target`, `mouse_ui`, or `ui_pos` rows. The left-drag windows inside Bubble2 trace were `11379..12722ms`, `13922..15177ms`, `16572..17353ms`, `18214..18719ms`, and `19472..19924ms`.
- Because `FUN_0045ef60` was attached but emitted no filtered rows in the 23:57 capture, the hook now also emits raw `mouse_dispatch_probe` rows for any `FUN_0045ef60` call during Bubble2 trace/left-button time. It also probes `FUN_00476a90` event `7`, `FUN_00465740` drag conversion state, and the room placement candidates `FUN_006f4b00` / `FUN_00707430`, which write a target object's `+0x84/+0x88` near the room Bubble2 setup code (`sdo.bin.c:433282-433317`, `sdo.bin.c:442562-442597`).
- Live capture from 2026-07-09 00:02 recorded `hook_ok:14`, `api_hook_ok:4`, `cursor_func:516`, `event_476a90:64406`, `event_465740:671`, `mouse_dispatch_probe:600`, `mouse_dispatch:1179`, `mouse_target_hook_ok:2`, and `mouse_target:75`. The left-drag windows inside Bubble2 trace were `10647..13033ms`, `14145..14495ms`, `15613..16118ms`, and `17156..17678ms`.
- The 00:02 capture confirms the event path reaches `FUN_0045ef60`, but `mouse_target` hit delegate wrapper vtable functions (`0x4b1f80` and `0x406c00`) instead of the real callback. Decompile shows those listener objects are wrappers shaped like `vtbl, owner, callback, extra` (for example `DAT_00ad20c4` and `DAT_00af8c48` creation sites around `sdo.bin.c:18173-18177`, `sdo.bin.c:20274-20278`). The hook now records each listener's `owner`, `cb`, and `extra`, and dynamically attaches the real `cb` as `mouse_callback`.
- Live capture from 2026-07-09 00:08 recorded `hook_ok:14`, `api_hook_ok:4`, `cursor_func:437`, `event_476a90:46272`, `event_465740:482`, `mouse_dispatch_probe:600`, `mouse_dispatch:474`, `mouse_callback_hook_ok:4`, and `mouse_callback:60`. The left-drag cursor windows were `9197..9590ms`, `10755..11233ms`, `12202..13426ms`, `14382..14995ms`, `16020..16695ms`, and `17291..18621ms`.
- The 00:08 callback layer hit callbacks `0x764a60`, `0x460810`, `0x765b30`, and `0x406fd0`, but every `mouse_dispatch`, `mouse_dispatch_probe`, and `mouse_callback` row had `leftDown=0`. This means the `FUN_00465740 -> FUN_0045ef60` path is release/end handling for the visible widgets captured here, not the continuous Bubble2 drag motion.
- Decompile follow-up found the Bubble2-like draggable object path in `FUN_00460c00` (`sdo.bin.c:80143-80242`) and its frame update in `FUN_00460ef0` (`sdo.bin.c:80251-80340`). On mouse down it sets `+0x334`, captures `DAT_00ca0b8c`, stores `cursorX - x_84` and `cursorY - y_88` in `+0x37c/+0x380`, then event `7` writes `x_84/y_88 = cursor - storedOffset`. On frame update a 50 ms timer gates the return/follower step, and each accepted tick moves by `0.006666667 * 50` (about one third) of the remaining distance. The 2026-07-09 remake attempt that mapped this directly onto Unity `PointerEventData` absolute cursor movement was worse in-game and was reverted; the next fix needs capture of the real official Bubble2 object `+0x84/+0x88` positions, not another UI-event guess.
- Live capture from 2026-07-09 07:46 used hook version `room-bubble-2026-07-09-capture-460c00-v2` and captured `FUN_00460c00` during active drag. Example object `0x224e6010`: mouse down at `(478,352)` with bubble `(0,-17.5)` stored `pressDx=478`, `pressDy=369.5`; subsequent event `7` rows write `x_84 = cursorX - 478`, `y_88 = cursorY - 369.5`. Unity must implement this as `startAnchoredPosition + (currentPointerLocal - startPointerLocal)` because `RectTransform.anchoredPosition` and `ScreenPointToLocalPointInRectangle` do not share the same absolute origin.
- Follow-up correction: `FUN_00460ef0` is called with `param_1 = objectBase + 0x110`, not the object base. Its follower fields therefore map to object-base offsets `+0x374` parent, `+0x378` follower flag, `+0x384/+0x388` home target, and `+0x390` vertical velocity. In follower mode it updates toward the previous bubble's actual `+0x84/+0x88`: `x += (parent.x - x) * 0.006666667 * 50`, `y += ((parent.y - y) - FUN_00460840(child)) * 0.006666667 * 50`. The update is gated by a `0x32` ms timer, so the Unity equivalent is about 20 accepted follow ticks per second, not 50fps smoothing. `FUN_00460840` returns `canvasHeight * 0.35` for the normal branch (`111 * 0.35 = 38.85` in the 07:46 Bubble2 capture) and adds `((value * 5 - 0x23) * 2)` for values >= 8. Unity maps that to each upper sent bubble following `previous.anchoredPosition + Vector2.up * spacing` during normal sent-chain drift. The typing bubble is not inserted as a sent-chain parent; it only acts as a temporary local push actor. When a sent bubble is dragged, upper bubbles remain linked to the dragged bubble, while lower bubbles continue drifting on their own home/rise targets. Dragging extends bubble lifetime but must not pause the global rise clock.

### TW client

- 原始反編譯: `H:\sdo_tw2\client.bin.c`
- 關鍵函式: `FUN_0073aa50`
- 男角 keyword table: `0x88aae8`，action id table: `0x88abc0`
- 女角 keyword table: `0x88ac98`，action id table: `0x88ad78`
- 聲音函式: `FUN_0073a8c0(actionId, isMale)`
- MOT lookup: `FUN_00712dd0(motCategory, actionId, gender)`

### CN client

- 原始反編譯: `H:\sdo_cn\sdo.bin.c`
- 關鍵函式: `FUN_00953630`
- 男角 keyword table: `PTR_DAT_00b8e6a0`，action id table: `DAT_00b8e778`
- 女角 keyword table: `PTR_DAT_00b8e850`，action id table: `DAT_00b8e930`
- 聲音函式: `FUN_009534a0(actionId, isMale)`
- MOT lookup: `FUN_0090ee60(motCategory, actionId, gender)`

使用的 dump helper:

```powershell
& 'H:\ghidra_12.0.4_PUBLIC\support\analyzeHeadless.bat' 'H:\sdo_tw2' 'sdo_tw2' -process 'client.bin' -readOnly -noanalysis -scriptPath 'H:\65_remake-roomchat\tools\ghidra_scripts' -postScript DumpRoomChatTables.java
& 'H:\ghidra_12.0.4_PUBLIC\support\analyzeHeadless.bat' 'H:\sdo_cn' 'sdo_cn' -process 'sdo.bin' -readOnly -noanalysis -scriptPath 'H:\65_remake-roomchat\tools\ghidra_scripts' -postScript DumpRoomChatTables.java
```

## Matching Rules

- 官方用 `lstrcmpiA(chatText, keyword)`，所以是大小寫不敏感的完整字串比對。
- 官方 room action path 沒有去掉 `/`，`/加油` 不是 room action。
- Remake 目前只做前後空白 trim，再照性別表查。
- 目前 mock room avatar 是 default female，`TryParseRoomAction(text)` 預設查女角表；男角表可用 `TryParseRoomAction(text, true, out action)` 查。
- 女角表有重複 keyword，例如 `呵呵` 同時出現在 action 0 與 action 1。官方線性掃表，先出現者獲勝，所以女角 `呵呵` 是 action 0。
- 官方 keyword 表沒有單字 `笑`。笑相關實際觸發字是 `哈哈`、`呵呵`、`嘻嘻`、`嘿嘿`、`:)`、`^_^` 等。

## Sound Mapping

| action id | female SE | male SE | note |
|---:|---|---|---|
| 0 | `SE\WOMAN_0.wav` | `SE\MAN_0.wav` | official switch case |
| 1 | `SE\WOMAN_1.wav` | `SE\MAN_1.wav` | official switch case |
| 2 | `SE\WOMAN_2.wav` | `SE\MAN_2.wav` | official switch case |
| 3 | `SE\WOMAN_3.wav` | `SE\MAN_3.wav` | official switch case |
| 4 | `SE\WOMAN_4.wav` | `SE\MAN_4.wav` | official switch case |
| 5 | `SE\WOMAN_5.wav` | `SE\MAN_5.wav` | official switch case |
| 6 | `SE\WOMAN_6.wav` | `SE\MAN_6.wav` | official switch case |
| 7 | none | none | no official sound case found |

## TW Keywords

### Male

| action id | keywords |
|---:|---|
| 0 | `快點`, `hurry up`, `趕緊的`, `快快快`, `速度`, `趕緊`, `準備`, `走`, `開`, `KKK`, `GO`, `READY` |
| 1 | `哈哈`, `挖哈哈`, `呵呵`, `挖哢哢` |
| 2 | `麼麼` |
| 3 | `生氣`, `怒了`, `發火`, `討厭`, `:(`, `欠扁` |
| 4 | `HI`, `你好`, `hello`, `哈嘍`, `嘿`, `嗨` |
| 5 | `昏`, `ft`, `暈`, `服了`, `orz`, `倒`, `OTL`, `@ @` |
| 6 | `再見`, `拜拜`, `88`, `bye`, `走了` |
| 7 | `打`, `扁人`, `我打`, `踢`, `T`, `T人`, `飛`, `拍磚`, `打PP`, `kick`, `874` |

### Female

| action id | keywords |
|---:|---|
| 0 | `呵呵`, `嘻嘻`, `嘿嘿`, `:)`, `^_^`, `橫橫`, `西西`, `襖襖`, `henhen`, `xixi`, `aoao` |
| 1 | `哈哈`, `挖哈哈`, `呵呵`, `挖卡卡`, `活活` |
| 2 | `麼麼` |
| 3 | `生氣`, `怒了`, `發火`, `討厭`, `:(`, `死開`, `去死`, `滾` |
| 4 | `HI`, `你好`, `hello`, `哈囉`, `好` |
| 5 | `再見`, `拜拜`, `88`, `bye`, `北北`, `BEI` |
| 6 | `難過`, `55`, `傷心`, `嗚嗚`, `死了`, `-_-||`, `|||`, `55555`, `T_T` |
| 7 | `打`, `扁人`, `我打`, `踢`, `T`, `T人`, `飛`, `拍磚`, `打PP`, `kick`, `874` |

## CN Keywords

### Male

| action id | keywords |
|---:|---|
| 0 | `快点`, `hurry up`, `赶紧的`, `快快快`, `速度`, `赶紧`, `准备`, `走`, `开`, `KKK`, `GO`, `READY` |
| 1 | `哈哈`, `挖哈哈`, `嗬嗬`, `挖咔咔` |
| 2 | `么么` |
| 3 | `生气`, `怒了`, `发火`, `讨厌`, `:(`, `欠扁` |
| 4 | `HI`, `你好`, `hello`, `哈喽`, `嘿`, `嗨` |
| 5 | `昏`, `ft`, `晕`, `服了`, `orz`, `倒`, `OTL`, `@ @` |
| 6 | `再见`, `拜拜`, `88`, `bye`, `走了` |
| 7 | `打`, `扁人`, `我打`, `踢`, `T`, `T人`, `飞`, `拍砖`, `打PP`, `kick`, `874` |

### Female

| action id | keywords |
|---:|---|
| 0 | `呵呵`, `嘻嘻`, `嘿嘿`, `:)`, `^_^`, `横横`, `西西`, `袄袄`, `henhen`, `xixi`, `aoao` |
| 1 | `哈哈`, `挖哈哈`, `嗬嗬`, `挖卡卡`, `活活` |
| 2 | `么么` |
| 3 | `生气`, `怒了`, `发火`, `讨厌`, `:(`, `死开`, `去死`, `滚` |
| 4 | `HI`, `你好`, `hello`, `哈啰`, `好` |
| 5 | `再见`, `拜拜`, `88`, `bye`, `北北`, `BEI` |
| 6 | `难过`, `55`, `伤心`, `呜呜`, `死了`, `-_-||`, `|||`, `55555`, `T_T` |
| 7 | `打`, `扁人`, `我打`, `踢`, `T`, `T人`, `飞`, `拍砖`, `打PP`, `kick`, `874` |

## MOT Lookup — RESOLVED

官方 room chat keyword table 只給 action id (0-7)。實際 MOT 由 `FUN_00953630` (CN) 再呼叫:

```c
// FUN_00953630 (sdo.bin.c:633962), param_1 = 玩家物件, param_2 = 聊天文字
uVar5 = keyword_id_table[matchIndex];        // 0-7 action id (性別各一張表)
if (FUN_00980c10(avatar) == 0) category = 0x18;   // 一般 avatar 走預設 category 24
else category = DAT_00b8ea10[ avatar[+0x87f] ];   // 特殊造型才查 10 筆造型表
motionRes = FUN_0090ee60(category, uVar5, gender);  // 回傳 motion 資源 id
FUN_0081a4b0(motionRes, ...);                       // 播放
FUN_009534a0(uVar5, gender);                        // 播聲音
```

- `FUN_0090ee60` (`sdo.bin.c:588686`, 真簽名 `(this, category, index, gender)`) 是對一個 vector-of-vectors
  取值: `bucket[category][index]`，`this` 是執行期建好的 motion table。
- `motCategory` 造型表 `DAT_00b8ea10` (10 筆，TW/CN 相同) = `[24, 25, 25, 24, 25, 0, 17, 17, 0, 17]`，
  由 avatar `+0x87f` 索引。`FUN_00980c10` 只檢查該 byte 是否 >= 0；一般預設 avatar 走 `0x18` (24)，
  且 `DAT_00b8ea10[0]` 也是 24。**所以標準 avatar 的 room chat 動作固定 category 24。**

`FUN_0090ee60` 取值的那張 motion table，就是 `Motion_LoadRestTable_004a3900` 從 offline exe `.data`
的 `~`-delimited 字串陣列 (male @VA 0x585668, female @VA 0x585988) 建的 55 個 bucket。**Category 24 (0x18)
剛好 8 筆，一 action id 一筆**，直接對上 keyword 表的 0-7，這就是缺的檔名映射:

| action id | female (WREST) | male (MREST) | 女語意 | 男語意 |
|---:|---|---|---|---|
| 0 | `wrest0058.mot` | `mrest0070.mot` | 呵呵/嘻嘻 | 快點/GO |
| 1 | `wrest0059.mot` | `mrest0071.mot` | 哈哈 | 哈哈 |
| 2 | `wrest0073.mot` | `mrest0083.mot` | 麼麼 | 麼麼 |
| 3 | `wrest0061.mot` | `mrest0073.mot` | 生氣 | 生氣 |
| 4 | `wrest0062.mot` | `mrest0074.mot` | 你好 | HI |
| 5 | `wrest0063.mot` | `mrest0075.mot` | 再見 | 昏 |
| 6 | `wrest0064.mot` | `mrest0076.mot` | 難過 | 再見 |
| 7 | `wrest0074.mot` | `mrest0084.mot` | 打 | 打 |

注意 action 5/6 男女語意不同 (女=再見/難過、男=昏/再見)，因為 keyword 表本來就性別各一份；motion table 也性別各一，
所以同一 action id 播不同 clip 是正確的。抽表 helper: `assets/sdox_offline/dump_motion_table.py` (bucket = category)。

Category 25 (0x19) 是 `marest/warest` 變體 (特殊造型)，同樣 8 筆; category 0/17 只有 1 筆 (idle)，
特殊造型指到那時只有 action 0 有效。標準 avatar 用不到，remake 只接 category 24。

Remake: `RoomChatCommand.RoomActions` 的 `FemaleMotion`/`MaleMotion` 已填上表; `RoomScreen.PlayRoomChatAction`
依 `Ctx.Session.Gender` 播對應 clip + SE; `MockChatService` 依性別查 keyword 表。全部 16 個 MOT 在
`Extracted/MOTION` 與 build `DATA/MOTION` 都在。
