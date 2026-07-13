# PlayerInformationDlg — 官方版面規格（逐字取自線上版 XML/AN）

來源檔（線上版 `閉撰敃氪/DatasSDO`，離線版 `sdox_offline` **沒有**這個對話框）：

| 檔案 | 用途 |
|---|---|
| `UI/PLAYERINFORMATIONDLG/PLAYERINFORMATIONDLG.XML` | 女版版面（1330 行；`WinPlayerInfo` 從第 772 行起） |
| `UI/PLAYERINFORMATIONDLG/PLAYERINFORMATIONDLG_MAN.XML` | 男版版面（1528 行；`WinPlayerInfo` 從第 914 行起） |
| `UI/PLAYERINFORMATIONDLG/*.AN` | 圖集裁切描述（純文字：`atlas.png (x,y,w,h)`） |
| `UI/PLAYERINFORMATIONDLG/BASEBOARD.DDS` / `BASEBOARD_MAN.DDS` | 主圖集（外框、分頁底、進度條） |
| `UI/PLAYERINFORMATIONDLG/BASEBOARD2.DDS` / `BASEBOARD2_MAN.DDS` | 分頁按鈕條、底部功能鈕 |
| `UI/PLAYERINFORMATIONDLG/EFFORT.DDS` | 戰績分頁的內底圖 + 努力/技巧切換鈕 |
| `UI/PLAYERINFORMATIONDLG/MASKPLAYERINFORMATIONDLG*.MSK` | 分頁按鈕的 1-bit 點擊/繪製遮罩 |

XML 是 **Big5**（台版），讀檔要 `decode('big5')`。座標系統是 800×600 視窗左上原點。

## 男女版是兩套皮

官方整個 UI 依**自己角色的性別**換皮（男藍女粉），連座標都不同：

| | 女版 | 男版 |
|---|---|---|
| 外框 `PlayerInformationDlg0` | `(93,56)` `BaseBoard.png (0,0,629,512)` | `(93,56)` `BaseBoard_man.png (0,0,625,502)` |
| 分頁按鈕條 | `(336,108)` | `(333,116)` |
| 分頁內容底 | `(336,147)` | 同左（男版素材 `_man`） |
| 底部功能鈕列 | `y=504` | `y=507` |
| 關閉鈕 `close` | `(662,62)` | `(662,73)` |

男版 XML 比女版多了 VIP／手鐲／證書／榮譽／天使等線上系統按鈕，離線重製一律不做。

## 分頁結構

`Tab playerTab` → 5 個 CheckBox `playerTabCheck0..4` **疊在同一個座標**，每個都畫「整條 350×39 的分頁條」，靠 `bgabnormal="MASK….msk"` 遮罩決定各自的可點區與繪製區。選中的那頁畫 `bgpushed`（亮），其餘畫 `bgnormal`（暗）。

| 分頁 | 視窗 | 底圖 | 內容 |
|---|---|---|---|
| 0 | `playerTabWindow0` | `PlayerInformationDlg34.an` = `BaseBoard.PNG (629,0,350,340)` | 基本資料：經驗、魅力、幸運、知名度、家族、年齡/星座/城市/QQ、線上時間 |
| 1 | `playerTabWindow1` | `PlayerInformationDlg43.an` = `BaseBoard.png (629,340,350,340)` | **戰績數據**（`SkillStat`）／努力值（`EffortStat`），由 `SkillBtn`/`EffortBtn` 切換 |
| 2 | `playerTabWindow2` | `PlayerInformationDlg54.an` / `playerfamilly1.an` | 勳章、家族徽章、段位 |
| 3 | `playerTabWindow3` | `PlayerInformationDlg79.an` | 拼圖 |
| 4 | `playerTabWindow4` | `ZoBG.an` | 星座精靈 |

分頁 2/3/4 全是線上系統（勳章、拼圖、星座），離線無資料來源。

## 左半邊（不分頁，永遠在）

```
AvtShow  AvatarShow   (105,111) 230×391     ← 即時 3D 角色
Label    chenghao     (132,111) 80×14  #faff74     稱號
Label    name         (132,129) 80×14  #faff74 粗   暱稱
Label    level        (132,144) 80×14  #faff74 粗   等級
Label    levelEmblem  (152,144) 25×25              等級徽章圖
Label    couplename   (240,129) …                  情侶（另一半）
```

## 分頁 1 — 戰績數據（`SkillStat`）

```
Label  performance    (429,166) 77×12  #9c1d23 置中     ← 舞技（一般模式）
Label  performanceau  (594,166) 77×12  #9c1d23 置中     ← 舞技（AU 模式）
Label  rank           (433,198) 230×12 #004f7c 靠左     ← 排名

CheckBox EffortBtn    (350,217)  Effort.png (322,30,109,30) / pushed (322,0,…)
CheckBox SkillBtn     (457,217)  Effort.png (322,90,109,30) / pushed (322,60,…)

Label SkillBg         (350,245)  Effort.png (0,0,322,190)   ← 六列數據的底圖

           文字 Label(#ffffff 靠左)      進度條 ProgressBar(236×19, 0..99)
勝率       winrate      (440,236)        pro_winrate      (433,258)
命中率     hitrate      (440,270)        pro_hitrate      (433,287)
PERFECT率  perfectrate  (440,303)        pro_perfectrate  (433,316)
GOOD率     goodrate     (440,339)        pro_goodrate     (433,345)
BAD率      badrate      (440,372)        pro_badrate      (433,374)
MISS率     missrate     (440,407)        pro_missrate     (433,403)
```

六條進度條共用 `PlayerInformationDlg65.an` = `BaseBoard.PNG (700,972,232,19)`。
文字 Label 預設 `labeltext="0.00%"` → 官方格式是兩位小數百分比。

（`familyoffer` / `privity` 兩個 Label 與 goodrate/badrate 重疊，是舊版遺留，實際不顯示。）

## 底部功能鈕（y=504 女／507 男）

| tabid | name | x | 何時出現 |
|---|---|---|---|
| 1 | `Dialog`（密語） | 108 | 看別人 |
| 2 | `AddFriend` | 208 | 看別人且非好友 |
| 3 | `DelFriend` | 208 | 看別人且已是好友 |
| 4 | `SendMail` | 308 | 看別人 |
| 5 | `AddEnemy` | 408 | 看別人 |
| 6 | `DelEnemy` | 408 | 看別人且已在黑名單 |
| 7 | `Confirm`（確定） | 608 | 永遠 |
| 8 | `BuyOtherEquipedButton` | 508 | 看別人（買他身上的裝備） |

離線重製只需要 `Confirm`（關閉）。
