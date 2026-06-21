# SDO 音訊系統（BGM / SE / 場景環境音）反編譯文件

原始遊戲 `sdo_stand_alone.exe`（offline 單機版）的音訊系統整理，供 Unity remake 直接消費。
所有結論皆以反組譯 + ProcMon 實機抓檔雙重驗證（2026-06-20）。

- 反編譯來源：`assets/sdox_offline/named/modules/gameplay/023_gameplay_00482340.c`（音訊管理器全在此）
- 觸發點：`assets/sdox_offline/named/modules/gameplay/021_gameplay_0046b8a0.c`
- 實機驗證工具：`procmon_tool/`（見 §6）

---

## 0. 一句話總覽

音訊有 **3 套**：①**BGM**（大廳/房間/待機的隨機循環背景樂）②**SE**（一次性音效：判定、UI、showtime、表情…）③**場景環境音**（特定場景的間歇氛圍音，如海底氣泡）。
三者都走同一個底層 FMOD 串流播放器，但用不同的上層函式、查不同的索引表。

---

## 1. 音檔索引表（兩張）

程式用 **index** 播音檔（非檔名）。兩張指標表都在 `.data`，字串在 `.rdata`。
（重建法：解析 PE，讀 `.data` 指標陣列 → 跟到 `.rdata` 字串。）

### SE 表 — VA `0x584068`（165 筆，index 0–164）
實際音檔在 `assets/sdox_offline/SE/`。關鍵索引：

| index | 檔案 | index | 檔案 |
|---|---|---|---|
| 0x00–0x0e | SE_0001…SE_0015 | 0x4d–0x53 | readygo_showtime / showtime / showtimeactive / showtimeboom / electricity / showtimewarning / showtimeend |
| 0x0f | VOICE_0001 | 0x54 | countroll |
| 0x10–0x24 | SE_0016…SE_0036 | 0x77–0x80 | 表情語音(man/woman dongganguangbo·thankyou·cold·kanjianwolema)+Flute_boy/girl |
| 0x25–0x40 | VOICE_0002…VOICE_0029 | 0x81–0x97 | UI(Buttonfloat/Menufloat/Interfacein/out/RoomReady/Start/Bubble…) |
| 0x41–0x4c | bingo/notbingo/pull*/fireworks/roll* | 0x98–0xa4 | community/紅包/活動 等 |

> ⚠️ index 15(0xf) 插了 VOICE_0001，所以 SE_00NN（NN≥16）的 index = NN。

### BGM 表 — VA `0x584300`（31 筆，index 0–30）
實際音檔在 `assets/sdox_offline/BGM/`。

| index | 檔案 | 用途 |
|---|---|---|
| 0–11 | BGM_TEACHING000/011/010/100/111/110/200/211/210/300/311/310 | 教學/Tutorial BGM |
| 12 (0xc) | BMG_000 | 回房/預設 |
| 13 (0xd) | BMG_001 | 固定指定曲 |
| 14–30 (0xe–0x1e) | BMG_002 … BMG_018（17 首）| 房間/待機隨機池 |

---

## 2. 播放機制（全部 __thiscall，gameplay state 物件）

| 函式 | VA | 用途 | 迴圈 |
|---|---|---|---|
| `SoundMgr_PlayStream` | 0x48a8e0 | 底層：FMOD `createStream`+`playSound` | mode 0x49=不循環 / 0x4a=循環 |
| `SeMgr_PlaySe(idx,a,loop)` | 0x48b570 | **一次性 SE**；10 聲道輪替 | 否（mode 0x49）|
| `SeMgr_PlayVoiceTimed(idx)` | 0x48b850 | **間歇定時 SE**（=場景環境音）| 否，但會定時重播（見 §3）|
| `SeMgr_PlayBgm(sel,loop)` | 0x48b7c0 | BGM 選曲 | 看參數 |
| `SeMgr_UpdateBgmLoop(sel)` | 0x48b950 | 每幀：BGM 播完自動換下一首 | 是（見 §4）|
| `SeMgr_PlayMaster(ptr)` | 0x48b660 | 歌曲主音軌（依檔名）| 是（mode 0x4a）|

> 全遊戲只有這個管理器碰 FMOD；**3D 場景載入器（scene 模組 000/029/030）不播任何音**。
> 場景環境音是由 §3 的「gameplay 每幀更新」依場景 id 觸發，**不是**場景載入器。

---

## 3. ★ 場景環境音（本文件重點）

### 對照表

全 44 場景中**只有 5 個**有環境音，其餘只有 BGM。皆 44.1kHz / 立體聲 / 16-bit。

| 場景 id | 場景內容 | 環境音檔 | 音長 | 氛圍 |
|:---:|---|---|:---:|---|
| **4** (0x4) | scn0004 海邊／沙灘 | `SE_0030.wav` | 15.02s | 海浪 |
| **12** (0xc) | scn0012 FIFA 足球場（日）| `VOICE_0017.wav` | 15.13s | 觀眾歡呼 |
| **13** (0xd) | scn0013 FIFA 足球場（夜）| `VOICE_0017.wav` | 15.13s | 觀眾歡呼 |
| **14** (0xe) | scn0014 海底 | `SE_0031.wav` | 8.60s | 水下氣泡 |
| **15** (0xf) | scn0015 花園／森林 | `SE_0033.wav` | 15.13s | 自然／鳥鳴 |

### 觸發碼
`Gameplay_Update_00478140`（[021_gameplay_0046b8a0.c:7452](../../assets/sdox_offline/named/modules/gameplay/021_gameplay_0046b8a0.c#L7452)），每幀、遊戲進行中（`2 < *(state+0x120)`）執行：

```c
switch (currentSceneId /* = *(DAT_00674f04 + 0x5c) */) {
  case 0x04:           PlayVoiceTimed(0x1e); break; // SE_0030
  case 0x0e:           PlayVoiceTimed(0x1f); break; // SE_0031  ← 海底
  case 0x0f:           PlayVoiceTimed(0x21); break; // SE_0033
  case 0x0c: case 0x0d:PlayVoiceTimed(0x34); break; // VOICE_0017
}
```

### 間隔邏輯（`SeMgr_PlayVoiceTimed`，[023:6303](../../assets/sdox_offline/named/modules/gameplay/023_gameplay_00482340.c#L6303)）

```
每幀呼叫；內部計時器：
  若 (距上次播放秒數 > nextInterval) 且 (聲道空閒):
      播放該 SE 一次（整段 = 音長）
      lastPlayTime = now
      nextInterval = 音長 + 隨機(0..29)        // rand % 0x1e
```

換算實際節奏：

| | 公式 | 海底 SE_0031 (8.6s) | 其餘 15s 檔 |
|---|---|---|---|
| 起點→起點 | 音長 + rand(0~29) | 8.6 ~ 37.6s | 15.1 ~ 44.1s |
| 兩聲間靜音 | rand(0~29) | 0 ~ 29s | 0 ~ 29s |

→ **不是循環、不是常駐**，是「播一段 → 隨機靜默 0~29s → 再播一段」的間歇氛圍音，疊在歌曲之上。

### 機器可讀（remake 用）
```json
[
  { "sceneId": 4,  "clip": "SE_0030.wav",   "lengthSec": 15.02 },
  { "sceneId": 12, "clip": "VOICE_0017.wav","lengthSec": 15.13 },
  { "sceneId": 13, "clip": "VOICE_0017.wav","lengthSec": 15.13 },
  { "sceneId": 14, "clip": "SE_0031.wav",   "lengthSec": 8.60 },
  { "sceneId": 15, "clip": "SE_0033.wav",   "lengthSec": 15.13 }
]
```

---

## 4. BGM 系統（大廳/房間/待機）

詳見 memory `sdo-bgm-table-and-loop`；摘要：

- `PlayBgm(sel)` 選索引：`sel<0`→12(BMG_000)；`sel==1`→13(BMG_001)；`0<sel<100`→隨機 BMG_002~011；`sel>=100`→直接 index `sel-100`（教學曲用此，如 100=TEACHING000）。
- `UpdateBgmLoop` 每幀跑：目前曲播完（或強制旗標）→ 隨機抽下一首 **BMG_002~018**（避免連抽同曲）→ **持續循環、自動換曲**。
- 進房間/待機（非打譜）→ 設旗標開播；**打譜開始 → BGM 靜音（旗標 0x3617d）**，改放歌曲主音軌。
- 結算畫面**不放 BGM**（見 memory `sdo-result-bgm-correction`）。

---

## 5. SE 一次性用途總表（反組譯 PlaySe 全 43 呼叫點）

| 情境 | index | 檔案 |
|---|---|---|
| Showtime/Fever | 0x4d–0x53 | readygo_showtime / showtime / showtimeactive / showtimeboom / electricity / showtimewarning / showtimeend |
| 勝/敗短曲 | 0xd / 0xe | SE_0014(贏) / SE_0015(輸) |
| 結算逐格計分 | 0x14 / 0x15 / 0x16 | SE_0020 / 0021 / 0022 |
| 進場景/狀態 | 0x3 / 0x8c / 0x84 | SE_0004 / Start / Interfacein |
| 譜面 Ready/Go | 0x26 (+0x4c) | VOICE_0003 (+readygo) |
| 表情 cut-in | 0x77–0x80 | dongganguangbo / thankyou / cold / kanjianwolema / Flute |
| UI 畫面切換 | 0x83 / 0x84 / 0x8d | Interfaceout / Interfacein / RoomReady |
| UI 下拉選單 | 0x89 | Bubble（純 UI，非海底氣泡）|

> 注意 0x89 `Bubble.wav` 是 UI 下拉音效，**不是**海底環境音；海底氣泡是 §3 的 SE_0031。

---

## 6. 驗證方法（ProcMon 工具）

`procmon_tool/`（見其 README）。在**管理員 PowerShell**：

```powershell
cd h:\65_remake\procmon_tool
.\capture.ps1 -Launch                 # 開始錄+啟動遊戲
# 進要測的場景、停幾秒、按 Enter → 印出開了哪些 .wav/.ogg
.\capture.ps1 -Highlight SE_0033.wav  # 指定檔
```

只錄 `sdo_stand_alone.exe` 行程（procmon-parser 產生 `.pmc` 過濾 + 丟棄其餘事件），Python 直接解析 `.pml`。

**已驗證**：進 scn0014（海底）跳舞，SE_0031.wav 在 22:26:32 被串流讀取 ~8.2s（一次，不循環），符合 §3 邏輯。
（教訓：環境音是間歇觸發，擷取要錄夠久跨過第一次計時器，否則會漏。）

---

## 7. 重製版實作建議

```csharp
// 純邏輯，可單元測試；接到場景進場流程
struct SceneAmbience { int sceneId; AudioClip clip; }
// 表：4→SE_0030, 12/13→VOICE_0017, 14→SE_0031, 15→SE_0033

void Update() {
    if (!gameplayActive || ambience == null) return;
    if (Time.time >= nextAt && !channel.isPlaying) {
        channel.PlayOneShot(ambience.clip);             // 播一次
        nextAt = Time.time + ambience.clip.length
                 + Random.Range(0, 30);                 // 音長 + rand(0~29)s
    }
}
```

- 只有上述 5 場景掛 ambience，其餘留空。
- 與 BGM 並存：環境音疊在 BGM/歌曲之上（獨立聲道）。
- 量到的音長見 §3 表；clip 直接用 `assets/sdox_offline/SE/` 的 wav。

---

## 相關
- 場景 id ↔ 舞台對照：[SDO_SCENE_MAPOBJ.md](SDO_SCENE_MAPOBJ.md)
- memory：`sdo-se-soundbank`、`sdo-bgm-table-and-loop`、`sdo-result-bgm-correction`
