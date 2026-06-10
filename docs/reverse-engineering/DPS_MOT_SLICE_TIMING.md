# DPS + MOT 切段時序總攬

## 1. 散裝 .DPS 真實佈局

只描述 `dps_slice_mot.write_dps` 產出的 **散裝 PAS00003** 格式

### 1.1 Header 276 bytes（**固定**）

| 偏移 | 大小 | 內容 | 說明 |
|------|------|------|------|
| 0 | 8 | ASCII `PAS00003` | magic |
| 8 | 4 | uint32 LE = 4 | dps_type |
| 12 | 256 | ASCII + null-pad | GN basename，**強制小寫**，例：`sdom1437k.gn\0\0…` |
| 268 | 4 | `0xFFFFFFFF` | sentinel |
| 272 | 4 | uint32 LE | row_count |

### 1.2 Row 317 bytes（在 `subs_per_row = 1` 時）

| 偏移 | 大小 | 內容 | 說明 |
|------|------|------|------|
| 0 | 4 | uint32 LE | `pre_a`（本 row 起拍 beat） |
| 4 | 4 | uint32 LE | `pre_b`（本 row 拍數） |
| 8 | 4 | uint32 LE | `pre_c`（sub_count，通常 1） |
| 12 | 16 | ASCII + null-pad | MOT 檔名，**強制小寫**，最多 15 字元 |
| 28 | 289 | bytes | `mid`（含 `v244` / `v248` / `v252`，見 §1.4） |

合計 317 bytes / row（12 + 16 + 289）。

### 1.3 Row preamble 三欄

| 偏移 | 名稱 | 單位 | 說明 |
|------|------|------|------|
| +0 | `pre_a` | beat（拍） | **本 row 起拍** = 上 row 的 `pre_a + pre_b`；嚴格連鎖，不可跳。 |
| +4 | `pre_b` | beat | **本 row 占的拍數**。≈ `seg_sec × eff_bpm / 60`。 |
| +8 | `pre_c` | int | 本 row 內的 sub_item 數量（`sub_count`）。1 = 標準。 |

### 1.4 sub-item.mid 關鍵欄位

`mid` 共 289 bytes（row 內自偏移 28 起）；其餘多為黑盒。下列偏移**相對 `mid` 起點**：

| 偏移 | 大小 | 內容 | 說明 |
|------|------|------|------|
| 0 | 244 | bytes | **未知**|
| 244 | 4 | uint32 LE | `v244`（本段 MOT 起始 frame） |
| 248 | 4 | uint32 LE | `v248`（本段 MOT 結束 frame，含端點） |
| 252 | 4 | float LE | `v252`（本段動畫秒數，**= nf/fps**，nf = `v248−v244+1`） |

> **eff_bpm = extra52 × 4 × 60 / duration**（取 e/n/h 中最大）。
> GN header offset 16 的 `bpm` 欄位**不影響** DPS 時序步調（已實測，見附錄）。
>
> **v252 ≈ nf/fps（嚴格對齊）**：實測 11223 row 0 `v252=3.034 / nf=91 / nf/30=3.0333`，差 0.0007s 為 float32 精度。讓 `v252` 嚴格 = `nf/fps` 才能避免「動畫播完等下一 row」的累積延遲。

---

## 2. 切割演算法（plan_slices）

實測決定步調的兩個欄位：**`extra52`、`duration`**。`bpm` 欄位、SongList 的 `bpm` / `max_measure` 不影響。

### 2.1 timing_mode

| 模式 | 公式 | 說明 |
|------|------|------|
| **`beat_snap`**（預設） | 拍對 `pre_b` → 累積式 `nf` → `v252 = nf/fps` 嚴格對齊 | 對 1437/11223 都 drift=0 |
| `frame_first` | `v244/v248` 先 → `v252 = nf/fps` → `pre_b` round | pre_b 不對拍 |
| `beat_first` | 舊版：`pre_b → v252 → round(v252×fps)` 幀 | 累積誤差大 |

### 2.2 beat_snap 詳算

```
eff_bpm    = extra52 × 4 × 60 / duration
pre_b      = round(seg_sec × eff_bpm / 60)
target_sec = pre_b × 60 / eff_bpm                   # 拍想要的秒數
nf         = round((cum_target+target_sec)×fps)     # 累積式 round
            - round(cum_target×fps)
v252       = nf / fps                               # 動畫長度 = row 持續
v244       = cur_frame
v248       = cur_frame + nf − 1
```

**「沒接縫但越跑越慢」修正歷史**：

舊版 `nf = round(seg_sec×fps) = 150`（5 秒），但 `v252 = pre_b×60/eff_bpm = 5.085s`。
動畫播 150 幀 = 5.000s 後等 0.085s row 才切 → 19 段累積 **+1.6s 落後**。
改 `v252 = nf/fps`、累積式 round → drift = 0.000s。

### 2.3 尾段補償

- `beat_snap`：補 `Σpre_b = total_beats`，**v252 仍 = nf/fps**（不對 v252 加秒差，避免再現等待）
- `frame_first`：把秒差併入最後 `v252`、回推 `pre_b`

### 2.4 連鎖規則

- `pre_a[i+1] == pre_a[i] + pre_b[i]`（必須）
- `v244[i+1] == v248[i] + 1`（不重不跳，loop_mot 時在 MOT 結尾歸零）
- `v248[i] == v244[i] + nf − 1`（含端點）
- **`v252[i] == nf/fps`**（避免等待 / 切幀）

---

## 3. 驗證工具

```
python tools/compare_dps_timing.py <dps> --bpm <eff_bpm> [--fps 30]
```

對 11223.DPS（eff_bpm=158.77）：file_inverse 命中 38/38、`beat_snap` 命中 38/38、v252 平均誤差 ~15ms。

---

## 4. 1223K 對照（驗證 eff_bpm 模型）

| 來源 | 1223K 數值 | DPS v252 ≤20ms 命中 |
|------|-----------|----------------------|
| header bpm | 160.00 | 8/45 |
| **eff_bpm = 86×4×60/130** | **158.77** | **33/45** |
| 反推最佳擬合 | 158.13 | (檔內 ~0.6 容差) |

---

## 附：使用者測試紀錄（sdom1437K，extra52=53, duration=99）

```
max_measure = extra52
extra52=53, duration=99
53*4*60/99 = 128.48  → 動作太快，間格被覆蓋
原 bpm=131           → 動作太慢，間格停頓
```

修改 SongList（**全部無效**）：max_measure / bpm / 全改 → 無變化。

修改 GN：
1. extra52 53→80：歌仍 1:40，但舞 **1:00** 跳完（幀被跳過，動作沒變快） → **與 extra52 有關**
2. (extra52=80) bpm 131→200：仍 1:00 播完 → **header bpm 無影響**
3. (extra52=80) duration 98→150：~1:40 播完，比原 80/98 慢

→ 結論：**步調 ∝ extra52 / duration**，與 GN bpm、SongList bpm 無關。