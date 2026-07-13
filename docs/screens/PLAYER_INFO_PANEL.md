# 房間右鍵點人 → 個人資訊/戰績面板

worktree `H:\65_remake-playerinfo`（branch `feat/player-info`）。

## 使用者看到什麼

在房間（3D 大廳）裡：

* **右鍵點任何一個人**（自己或房裡其他人的 3D 角色）→ 跳出官方的「个人信息」對話框。
* **右鍵點左上角的頭像框** → 同一個面板（官方兩種點法都有）。
* 面板預設停在 **「技术统计」** 分頁 —— 就是戰績數據：
  勝率 / 命中率 / Perfect率 / Cool率 / Bad率 / Miss率，各一個兩位小數百分比 + 一條進度條。
* 另一個分頁「基本信息」是官方的個人資料頁。
* 左半邊是那個人的**即時 3D 全身**（穿他當下的穿搭），上面是稱號/名字/等級。
* ✕ 或「確定」或 ESC 關閉。

## 資料從哪來

| 欄位 | 本機玩家 | 房裡其他人 |
|---|---|---|
| 名字 / 等級 | `profile.json`（`UserProfile.name` / `.level`） | `RoomOccupants`（依 slot 固定的假名單） |
| 六項比率 | `profile.json` 的 `stats`（**真的累計**） | `MockPlayerStats.For(id)`（依 id 決定性生成） |
| 3D 角色 | 目前穿搭（`WardrobeStore.ResolveEquippedParts`） | 預設女角 |

### 累計戰績是新加的

官方這些數字是**伺服器統計後下發**的 —— 離線 EXE 連 `PlayerInformationDlg` 的程式碼都沒有
（跟商城道具名一樣是線上限定）。所以離線版改成本機累計：

* `Sdo.Settings.PlayerStats`（純邏輯，有測試）：`games / wins / perfect / cool / bad / miss / bestScore / totalScore`
  → 算出六項比率。**命中率 = (Perfect+Cool+Bad) / 總 note**（即「非 MISS」）。
* 存在 `profile.json` 的 `stats` 欄位（舊檔沒有 → `Sanitize()` 補一個空的）。
* 寫回時機：一局結算時 `ScreenGameplay.ShowResultPanel` → `onRoundStats` 回呼 → `FrontendApp.RecordRoundStats`
  → `ProfileManager.Save()`。**自由模式不計**（跟 G幣/EXP 同一條規則）。
  （`onRoundStats` 是個回呼欄位，維持 `Sdo.Game` 不直接相依 `Sdo.Settings` 的既有約定，同 `laneKeyOverride`。）

### 房裡的其他人是假的

離線 EXE 的房間本來**只有房主一個人**（其他人是連線時伺服器用 move packet 放進來的）。
沒有人可以點，「右鍵選人」就沒有意義 → `RoomScene3D.fillTestAvatars` 由 `false` 改成 **`true`**：
放 5 個舞者（隨機走得到的位置）+ 10 個旁觀者（逆向出來的 `.data` 座標）。
他們的身分是決定性的假資料，接上線之後把 `RoomOccupants` / `MockPlayerStats` 換成伺服器名單即可。

## 沒做的（官方有、離線無資料源）

* 分頁 **赛事信息 / 拼图 / 星座**（段位、勳章、家族徽章、拼圖、星座精靈）—— 全是線上系統。
* 「基本信息」頁的 天使等级 / TP值 / 经验值 / 魅力值 / 幸运值 / 知名度 / 家族 / 年龄 / 星座 / 城市 / QQ 号 / 社交值
  —— 欄位名烘在官方底圖上（忠於原樣），值留白。這一頁離線只填得出 MVP（= 第一名場次）。
* 頂端的 **超舞战绩 / 劲舞战绩 / 目前排名** —— 伺服器算的舞技分與全服排名，沒有離線對應物 → 留白。
* 底部的 密語 / 加好友 / 寄信 / 黑名單 / 買他身上的裝備 —— 線上功能，只留「確定」。
* 男版藍色皮（`PLAYERINFORMATIONDLG_MAN.XML`）—— 官方依自己角色性別換整套 UI 皮，但重製版其餘 UI
  （ROOM/ROOMDLG/SHOP/OPTIONDLG…）一律用女版，這裡跟著一致。

## 一個刻意的偏離

官方 XML 裡那六個百分比 Label 的 y（236/270/303/339/372/407）**排不成格子**，第一列還會落到「统计明细」
按鈕上；而六條進度條的 y（258/287/316/345/374/403）剛好是 29px 一列、對得上底圖的六個凹槽。
判定是舊版面留下的殘值 → 文字改壓在自己那條 bar 上（x 仍用官方的 440）。

## 相關檔案

| | |
|---|---|
| 官方版面規格 | [docs/reference/PLAYER_INFO_DLG.md](../reference/PLAYER_INFO_DLG.md) |
| 用到哪些官方檔 | [docs/USED_ASSETS_PLAYERINFO.md](../USED_ASSETS_PLAYERINFO.md) |
| 素材載入 | `Assets/Scripts/UI/Util/PlayerInfoArt.cs` |
| 面板 | `Assets/Scripts/UI/Screens/PlayerInfoModal.cs` |
| 右鍵 picking | `Assets/Scripts/UI/Screens/RoomScreen.cs`（`HandleRightClickPerson`）+ `Game/RoomOccupant.cs` |
| 累計戰績 | `Assets/Scripts/Sdo.Settings/PlayerStats.cs` |
| 假身分/假戰績 | `Assets/Scripts/UI/Services/RoomOccupants.cs`、`MockPlayerStats.cs` |
| data root link farm | `tools/link_data_root.ps1` |
| 測試 | `Assets/Tests/EditMode/PlayerStatsTests.cs`、`PlayerInfoArtTests.cs` |
