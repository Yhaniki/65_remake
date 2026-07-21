# NX 工具箱 — NXPatch 私服客戶端的譜面格式與破解

`.nx` 是 **NXPatch.exe**（Super Dance Online 私服/線上客戶端）的譜面容器。
它跟舊的 `k.gn` 很像但**不一樣**，而且多了兩個功能：**捲動速度變化**與**炸彈**。
這個資料夾放逆向的成果：格式規格、啟動繞過、以及取得解密譜面的工具。

## 先講結論（很重要）

> **光有 `.nx` 檔案，離線解不開。**
> 譜面用的還是遊戲原本的 0x3D09 LCG，但**解密金鑰不在檔案裡** —— 是進歌時由**連線提供**、
> 放在執行期記憶體的。舊 `.gn` 的金鑰在檔頭（或有 148-seed pool 可暴力），新容器把它搬走了，
> 這是刻意的 DRM。
>
> 想拿到明文譜，**只能連上真伺服器實際玩到那首歌**，用 `dump_chart.py` 在執行期把解密後的
> buffer 撈出來。細節與已排除的所有猜測見 [`NX_FORMAT.md`](NX_FORMAT.md) §2。

## 檔案

| 檔案 | 用途 |
|------|------|
| [`NX_FORMAT.md`](NX_FORMAT.md) | **格式規格**：容器版面、StepFile 表頭、加密機制與金鑰來源、note 格式（frame/note type）、**捲動速度 frame_type 33**、**炸彈 note_type 1** |
| [`NXSTART.md`](NXSTART.md) | **啟動握手怎麼繞過**：LaunchStamp + seal + 共享記憶體，含校驗算法與 Python 版 |
| [`NXStart.cs`](NXStart.cs) | 最小替代啟動器原始碼（C#，來源見 NXSTART.md） |
| [`dump_chart.py`](dump_chart.py) | **主力工具**：attach 執行中的 NXPatch，hook 解密函式，把明文譜面 dump 下來 |
| [`decode_chart.py`](decode_chart.py) | 解析 dump 出來的明文譜：frame/note 統計、**捲動速度表**、**炸彈表** |
| [`dump_nxpatch_image.py`](dump_nxpatch_image.py) | spawn NXPatch 並 dump 記憶體映像（靜態逐位址分析用；平常不需要） |

需求：`pip install frida`（`dump_*.py`）。`decode_chart.py` 只用標準庫。

## 取得解密譜面的流程

```
1. 照平常方式啟動遊戲並登入（要連得上私服）
2. python dump_chart.py            ← attach 到執行中的 NXPatch.exe
3. 進遊戲，實際進入你要的那首歌
4. 產出 dumped_seed<seed>_len<n>.bin（明文譜），終端也會印出 seed
5. python decode_chart.py dumped_*.bin --diff hard
```

`decode_chart.py` 對 sdom2818（"3" by Laur）的實測輸出：

```
file_id=12818  BPM=210.0  levels=[16, 23, 36]
note_count=[1124, 1286, 1611]（含炸彈）
note_type : {'一般音符': 1525, '炸彈': 20, '長條頭': 33, '長條尾': 33}   ← 1525+33+33+20 = 1611 ✓
捲動速度 frame_type 33（80 個；base 1000 = ×1.0）
   小節  1: b1.0=2000(×2)
   小節  7: b1.5=250(×0.25)[線性5.5拍]
   ...
炸彈 note_type 1（20 顆）
```

## 主要發現摘要

- **容器**：`0x00` 起 6 個資源名 → `0x1C8` StepFile 表頭（**明文**）→ `0x2F4` 起加密 blob。
  `music\sdomNNNNK.gn` 也是同一個格式（副檔名叫 .gn，內容不是舊 ddrm/sdom）。
- **加密**：0x3D09 LCG；blob 在 `0x2F4`、長度 `filesize−0x2F4`；seed 來自執行期 `[ctx+0x90]` 的
  ddrm 標頭（**server-provided**）。`NXPatch.exe` 相關位址：解密 `0xb1c950`、容器讀取 `0xd6e9b0`、
  舊 ddrm 讀取 `0xd6c360`、LoadNote `0xd67220`。
- **捲動速度** = `frame_type 33`：低16位 = 速度值（**1000 = ×1.0**），高16位 = **線性變速時長**
  （1/48 拍，0 = 瞬間）。已用官方編輯器的位置交叉驗證吻合。
- **炸彈** = 音軌幀的 `note_type 1`（要避開，不是拿來打的），`note_count` 有把它算進去。
- **NXPatch.exe 沒有真的加殼**，主 `.text` 是明文可讀，所以載入/解密流程能純靜態分析出來。

## 重製版接在哪

`Sdo.Osu/GnChart.cs`（解析）、`Sdo.Osu/OsuBeatmap.cs`（`ScrollSpeeds`/`CurrentScrollSpeed`）、
`Game/ScreenGameplay.cs`（`ScrollPx` 套捲動、`TickBombs`/`ExplodeBomb` 炸彈）。
對照表見 [`NX_FORMAT.md`](NX_FORMAT.md) §4。
