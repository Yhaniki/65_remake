# SDO 血量（HP）反編譯考據 — 確定版

> 來源：`assets/sdo_stand_alone.exe`（md5 `fe49f9e3…`）+ Ghidra 反編譯 `assets/sdo_stand_alone.exe.c`。
> 數值以 capstone 反組譯原始位元組核對過，非推測。
> 對照計分：[SDO_SCORE_FORMULA.md](SDO_SCORE_FORMULA.md)。

## 一、血量變數位置（單人玩法，驅動 `myhp_progress` 血條）

血條是 `GamePlay\DdrGamePlay.xml` 裡名為 **`myhp_progress`** 的進度條（圖案式、不顯示數字），
其填充值每幀直接 = 玩家物件的 HP。

| 項目 | 位置 | 備註 |
|------|------|------|
| 當前 HP | **`*(int*)( *(gameObj+0xd8) )`**（玩家物件 offset 0x0） | 驅動 `myhp_progress` |
| 最大 HP | 玩家物件 offset 0x4 = **1000** | 開場 `HP = maxHP` |
| 玩家物件陣列 | `gameObj + 0xdc + i*4` | 當前玩家指標存在 `+0xd8` |
| HP 內部範圍 | **[-150, 1000]**（`-0x96` ~ maxHP） | 顯示時 +150 偏移 |
| 開場滿血 | `player[0] = player[1] = 1000`（行 86022-86026） | |
| 無敵旗標 | 玩家物件 offset 0x274（`[0x9d]`）≠0 時 HP 不變 | |

> ⚠️ 舊版文件把 `0x10944`（0~600）當血量是**錯的**。`0x10944` 其實是
> **音符板(notes board)的動畫值 / PK 雙人血條**（`bloodPKTeam1/2.an`），由計時器固定速率變動，
> 與單人打擊判定無關。真正的單人血量是上表的 `player[0]`。

## 二、判定 → 加減血（核心公式）

每次判定呼叫 **`FUN_004a6470(player, delta)`**（位址 `0x4a6470`）：

```c
HP = HP + (player[0x9d]==0 ? delta : 0);   // 無敵模式時 delta 失效
if (HP > maxHP) HP = maxHP;                 // 上限 = player[1] = 1000
if (HP < -150)  HP = -150;                  // 下限 = -0x96
// 即： HP = clamp(HP + delta, -150, 1000)
```

判定等級 → delta 對照（判定函式 `0x470?`，行 84854-84885）：

| 判定等級 `grade` | 加減血 delta 來源 | 結算計數器 |
|------|------|------|
| **4**（Perfect，最緊時窗）| `gameObj+0x980` | `+0x830` |
| **3**（Cool）| `gameObj+0x984` | `+0x834` |
| **2**（Bad）| `gameObj+0x988` | `+0x838` |
| **其他**（1/0 = Miss）| `gameObj+0x98c` | `+0x83c` |

> grade 由時間窗函式 `FUN_0048c4a0`（`0x48c4a0`）回傳：誤差越小回傳值越大，4=最佳。

## 三、各判定的 delta 實際數值（依難度）

由 **`FUN_0046ca60(this, level)`**（`0x46ca60`，行 82709-82735）設定，
`level = *(byte*)(DAT_00674f04 + 0x75)`（全域設定，值 0/1/2）：

| 判定 | level 0 | level 1 | level 2 |
|------|--------:|--------:|--------:|
| **Perfect** (grade 4) | **+6**  | **+4**  | **+2** |
| **Cool**    (grade 3) | **+4**  | **+2**  | **+1** |
| **Bad**     (grade 2) | **−10** | **−7**  | **−5** |
| **Miss**    (else)    | **−50** | **−40** | **−30** |

（原始位元組：level0 = `06 04 / f6 ce` → 6,4,−10,−50；level1 = `04 02 / f9 d8` → 4,2,−7,−40；
level2 = `02 01 / fb e2` → 2,1,−5,−30。）

## 四、本專案採用

直接照搬上表即可，套用於 `Sdo.Ruleset.HealthProcessor`：

- 初始 HP = 1000（滿）；最大 1000；最小 −150（夾住）。
- 每次判定 `HP += delta(判定, level)`，依上表。
- **失敗條件**：以血條顯示為準（HP 觸底 −150 = 空血）。判定渲染 `(HP+150)` 為長度。
- `level` 是系統設定（`DAT_00674f04+0x75`），不跟著歌曲；osu `.osu` 的 HPDrainRate 一律忽略。
- 無持續被動 drain（單人血量只在判定時變動，與計時器無關）。

> 反組譯方法：`assets/sdox_offline/sdo_stand_alone.exe` 以 capstone 反組譯
> `0x4a6470 / 0x46ca60 / 0x48c4a0` 及呼叫點核對。注意 Ghidra 會丟棄 x87 浮點運算，
> 且 `FUN_00516244` 是 `round()` 取整工具（非亂數），分析時需回看組合語言。
