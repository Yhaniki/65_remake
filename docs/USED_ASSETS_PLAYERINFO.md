# 個人資訊/戰績面板 — 用到的官方檔案

這個 worktree (`feat/player-info`) 把 data root 指到一棵 **link farm**（離線為底、線上補缺），因為
「房間右鍵點人 → 個人資訊/戰績」用到的整包素材 **離線版沒有**。這份文件記錄實際吃到哪些檔，
未來整合時可以只抽這些檔進乾淨的資料樹。

## data root 怎麼組的

```
powershell -File tools/link_data_root.ps1        # 重跑加 -Force
```

* 產物：`H:\65_remake\assets\DATA_LINK`（每個 top-level 都是 junction，不佔空間）
* 指向：worktree 根的 `data_root.txt`（`SdoExtracted.ConfiguredRoot` 會讀，已 gitignore）
* 規則：**離線 `sdox_offline/Extracted` 優先，線上 `閉撰敃氪/DatasSDO` 只補離線沒有的**
  * 不整棵指向線上，是因為線上 `DatasSDO` 有些 `wdance*.mot` 是空檔/損毀（舞蹈會凍結），
    離線 `Extracted` 的同名檔是好的。
  * `PROFILE/` 是真目錄（遊戲要寫存檔），不能 junction 到唯讀來源。
* 結果：67 個 top-level、110 個 `UI/` 子夾（離線 25 + 線上 85）。

## 這個功能真正讀到的檔

全部在 `DatasSDO/UI/PLAYERINFORMATIONDLG/`，**共 4 張圖集 + 19 個 .an，約 840 KB**。
（`.an` 是純文字的圖集裁切描述：`atlas.png (x,y,w,h)`。）

### 圖集（真正的像素資料）

| 檔案 | 大小 | 用途 |
|---|---|---|
| `BASEBOARD.PNG` | 186 KB | 女版粉色外框、兩個分頁的內容底圖、進度條填充 |
| `BASEBOARD2.PNG` | 117 KB | 分頁按鈕條（基本信息 / 技术统计 / 赛事信息） |
| `BASEBOARD_MAN.PNG` | 504 KB | 關閉鈕 ✕ 與「確定」鈕 —— **官方女版 XML 就是從男版圖集裁這幾顆共用鈕**，不是筆誤 |
| `EFFORT.PNG` | 32 KB | 技术统计頁的六列底板（胜率/命中率/Perfect/Cool/Bad/Miss 的欄名烘在上面）+ 成就/统计明细 切換鈕 |

### .an（裁切描述）

| .an | → 圖集 (x,y,w,h) | 用途 |
|---|---|---|
| `PlayerInformationDlg0` | BaseBoard (0,0,629,512) | 外框 @ (93,56) |
| `PlayerInformationDlg14/15/16` | BaseBoard_man (972,618/647,29,29) | 關閉鈕 normal/hover/pushed @ (662,62) |
| `PlayerInformationDlg29/30/31` | BaseBoard_man (0,512/547/546,86,35) | 確定鈕 @ (608,504) |
| `PlayerInformationDlg4` / `6` | BaseBoard2 (0,396/240,350,39) | 分頁「基本信息」暗/亮 @ (336,108) |
| `PlayerInformationDlg7` / `9` | BaseBoard2 (0,435/279,350,39) | 分頁「技术统计」暗/亮 @ (336,108) |
| `PlayerInformationDlg34` | BaseBoard (629,0,350,340) | 基本信息 內容底 @ (336,147) |
| `PlayerInformationDlg43` | BaseBoard (629,340,350,340) | 技术统计 內容底 @ (336,147) |
| `SkillBg` | Effort (0,0,322,190) | 六列底板 @ (350,245) |
| `EffortBtn1` / `EffortBtn2` | Effort (322,30/0,109,30) | 「成就」鈕 @ (350,217) |
| `SkillBtn1` / `SkillBtn2` | Effort (322,90/60,109,30) | 「统计明细」鈕 @ (457,217) |
| `PlayerInformationDlg65` | BaseBoard (700,972,232,19) | 六條進度條的填充（共用一張） |

版面來源（**只在開發時人工讀，執行期不解析**）：
`UI/PLAYERINFORMATIONDLG/PLAYERINFORMATIONDLG.XML`（Big5/GBK，`WinPlayerInfo` 從第 772 行起）。
座標已逐字抄成 `PlayerInfoModal` 的常數，見 [PLAYER_INFO_DLG.md](reference/PLAYER_INFO_DLG.md)。

### 沒用到（但同資料夾裡的）

`PLAYERINFORMATIONDLG/` 共 1705 個檔，其餘 ~1690 個全是官方線上系統的素材 ——
星座精靈（Zo*）、拼圖、勳章（xunzhang）、家族徽章、天使、寵物、努力值道具、推廣員（Accepter/Spreader）、
魅力/幸運星星、段位（duanwei）…… 離線都沒有資料來源，**一個都沒讀**。
未來要抽乾淨資料，只要上表這 23 個檔。

## 已登記到哪裡

* `UsedAssetsProbe.UiDirs` 加了 `"UI/PLAYERINFORMATIONDLG"` —— 不加的話死檔清理 (`prune_dead_data.ps1`) 會把它整包當死檔搬走。
* `tools/package_build.ps1` 加了一行把它 overlay 進 `DATA/UI/PLAYERINFORMATIONDLG` —— 不加的話打包版的
  `PlayerInfoArt.Available` 會是 false，右鍵不會開面板（不會破圖，就是安靜地不開）。

## 驗證「到底吃了哪些檔」

```
set SDO_TRACE_LOADS=H:\tmp\loads.txt      # 每次嘗試載入貼圖都會 append 一行絕對路徑
```
跑一輪、開面板、切分頁，然後看 `loads.txt` 裡 `PLAYERINFORMATIONDLG` 的行 —— 應該只出現上表那 4 張圖集。
（`.an` 本身不經過 `LoadTexture`，不會出現在 trace 裡。）
