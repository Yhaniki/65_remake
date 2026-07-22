#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
measure_song_offsets — 把「譜面說哪裡該有打點」跟「音檔實際的瞬態在哪」對起來，量出每首歌的偏差。

背景：
  編輯器裡把波形疊在音符上看，會發現音符普遍比音樂的瞬態早到 30~40ms。這個偏差**不是**音訊延遲——
  波形和音符畫在同一支時鐘上（Y = f(chartMs - now)），now 相減時消掉，所以任何 clock offset（global、
  驅動延遲）都動不到它們的相對位置。它是**譜面時間軸跟音檔之間真正的錯位**。

  但錯位是「每首各自的」還是「全部一樣的系統性偏差」？這決定了修法完全不同：
    • 系統性（分布集中在同一個值）→ 是 .gn→ms 換算或 type-10 marker 的 bug，要在程式裡修一次；
      把同一個值寫進 2175 首歌的 per-song offset，是拿 per-song 欄位去補全域 bug。
    • 各自不同（分布很散）→ 才是真正的 per-song offset。
  所以這支預設**只量、不寫**，先看分布。

方法：
  1. 音符時間由 tools/gn_note_export（直接編譯遊戲用的 Sdo.Osu.GnChart）導出 → gn_notes.json。
     不在這裡重寫 .gn 解析器：自己寫一份的話，量到的偏差可能只是兩個 parser 的分歧。
  2. 音檔算 onset strength envelope（spectral flux：STFT 幅度的正向差分沿頻率加總）。
     用 flux 而不是振幅包絡：打擊點是「頻譜突然變化」，在有持續音（人聲/長音）的段落，振幅包絡幾乎看不出鼓點。
  3. 把音符打點做成脈衝序列，跟 flux 做互相關，掃 lag ∈ [-LAG, +LAG]，取峰值。
     lag > 0 = 音檔的瞬態比譜面說的**晚** → 要把音樂往前拉 → offsetMs = -lag。
  4. 信心度 = 峰值 / 次峰值（peak-to-sidelobe）。低於門檻的當「量不準」，不採用。

用法：
  python tools/measure_song_offsets.py                       # 全部量一遍，印分布（不寫任何檔）
  python tools/measure_song_offsets.py --limit 200           # 先抽 200 首看看
  python tools/measure_song_offsets.py --csv out.csv         # 逐首結果存 CSV
  python tools/measure_song_offsets.py --apply --max-abs 60  # 確認過分布之後才用：|lag|<=60 且信心夠 → 寫回 song_table.csv
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import os
import sys
from pathlib import Path
from typing import Dict, List, Optional, Tuple

import numpy as np
import soundfile as sf

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
SA = REPO / "65" / "My project" / "Assets" / "StreamingAssets"

sys.path.insert(0, str(HERE))
import song_table as st  # noqa: E402

DEFAULT_NOTES = HERE / "kgn_export" / "gn_notes.json"

# ---- 分析參數 ----
SR = 22050            # 重採樣目標：對打點偵測夠用，速度快一倍
HOP = 128             # 5.8ms 一格 —— 要比我們想量的 30ms 細很多
NFFT = 1024
LAG_MS = 200.0        # 掃描範圍 ±200ms（比任何合理的錯位都寬）
MAX_ANALYZE_SEC = 90.0  # 只看前 90 秒：夠 100+ 個打點，而且避開後段可能的變速/接歌


def onset_envelope(path: Path) -> Tuple[np.ndarray, float]:
    """回傳 (onset strength 包絡, 每格幾毫秒)。"""
    x, sr = sf.read(str(path), always_2d=True, dtype="float32")
    x = x.mean(axis=1)                                   # 轉單聲道
    if sr != SR:                                         # 線性重採樣（onset 偵測不需要高品質濾波）
        n = int(len(x) * SR / sr)
        if n <= 0:
            return np.zeros(0, np.float32), HOP / SR * 1000.0
        x = np.interp(np.linspace(0, len(x) - 1, n), np.arange(len(x)), x).astype(np.float32)
    x = x[: int(MAX_ANALYZE_SEC * SR)]
    if len(x) < NFFT:
        return np.zeros(0, np.float32), HOP / SR * 1000.0

    # STFT（手刻，不依賴 librosa）
    win = np.hanning(NFFT).astype(np.float32)
    n_frames = 1 + (len(x) - NFFT) // HOP
    idx = np.arange(NFFT)[None, :] + HOP * np.arange(n_frames)[:, None]
    frames = x[idx] * win
    mag = np.abs(np.fft.rfft(frames, axis=1)).astype(np.float32)

    # spectral flux：只取「變大」的部分（打擊 = 能量突然增加），沿頻率加總
    diff = np.diff(mag, axis=0)
    flux = np.maximum(diff, 0.0).sum(axis=1)
    flux = np.concatenate([[0.0], flux]).astype(np.float32)

    # 減掉局部中位數 → 壓掉持續音的底、留下尖峰
    k = 32
    pad = np.pad(flux, (k, k), mode="edge")
    local = np.array([np.median(pad[i : i + 2 * k + 1]) for i in range(len(flux))], np.float32)
    flux = np.maximum(flux - local, 0.0)

    s = flux.std()
    if s > 1e-9:
        flux /= s
    return flux, HOP / SR * 1000.0


def best_lag(flux: np.ndarray, frame_ms: float, note_ms: np.ndarray,
             music_start_ms: float) -> Optional[Tuple[float, float, int]]:
    """
    互相關求 lag。回傳 (lag_ms, 信心度, 用到的打點數)；打點太少 → None。

    音符的譜面時間 → 音檔時間：clipMs = noteMs - musicStartMs（type-10 的無聲數拍）。
    """
    if len(flux) == 0:
        return None
    clip_ms = note_ms - music_start_ms
    horizon = len(flux) * frame_ms - LAG_MS
    clip_ms = clip_ms[(clip_ms >= LAG_MS) & (clip_ms < horizon)]
    if len(clip_ms) < 12:                                  # 打點太少 → 峰不可信
        return None

    lag_steps = int(round(LAG_MS / frame_ms))
    lags = np.arange(-lag_steps, lag_steps + 1)
    base = np.round(clip_ms / frame_ms).astype(np.int64)

    # score(lag) = Σ flux[打點格 + lag]。峰在哪，音檔的瞬態就整體偏移多少。
    score = np.empty(len(lags), np.float64)
    for i, L in enumerate(lags):
        idx = base + L
        score[i] = flux[idx].sum()

    pk = int(np.argmax(score))
    peak = score[pk]
    if peak <= 0:
        return None

    # 信心度 = 峰 / 次峰（把主峰附近 ±25ms 遮掉再找次高）——峰越突出，這個 lag 越可信。
    guard = max(1, int(round(25.0 / frame_ms)))
    masked = score.copy()
    masked[max(0, pk - guard) : pk + guard + 1] = -np.inf
    side = masked.max()
    conf = float(peak / side) if side > 0 else float("inf")

    # 拋物線內插：峰值落在格與格之間，不內插的話解析度只有 5.8ms
    lag = float(lags[pk])
    if 0 < pk < len(score) - 1:
        y0, y1, y2 = score[pk - 1], score[pk], score[pk + 1]
        denom = y0 - 2 * y1 + y2
        if abs(denom) > 1e-12:
            lag += 0.5 * (y0 - y2) / denom
    return lag * frame_ms, conf, len(clip_ms)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--notes", default=str(DEFAULT_NOTES), help="gn_note_export 的輸出")
    ap.add_argument("--music", default="H:/65_remake_clean/DATA/MUSIC", help="MUSIC 目錄（.ogg）")
    ap.add_argument("--table", default=str(st.DEFAULT_CSV), help="song_table.csv")
    ap.add_argument("--limit", type=int, default=0, help="只量前 N 首（0 = 全部）")
    ap.add_argument("--csv", default="", help="逐首結果寫成 CSV")
    ap.add_argument("--apply", action="store_true",
                    help="把結果寫回 song_table.csv 的 offsetMs（**先看過分布再用**）")
    ap.add_argument("--max-abs", type=float, default=60.0, help="--apply 時：|lag| 超過這個值視為異常，不設定")
    ap.add_argument("--min-conf", type=float, default=1.30, help="--apply 時：信心度低於這個值視為量不準，不設定")
    ap.add_argument("--min-notes", type=int, default=24, help="--apply 時：可用打點數下限")
    args = ap.parse_args()

    doc = json.loads(Path(args.notes).read_text(encoding="utf-8"))
    songs = doc["songs"]
    if args.limit > 0:
        songs = songs[: args.limit]
    music = Path(args.music)

    rows: List[Dict] = []
    no_audio = 0
    for i, s in enumerate(songs):
        stem = s["gn"]
        ogg = music / f"{stem}.ogg"
        if not ogg.exists():
            no_audio += 1
            continue
        try:
            flux, frame_ms = onset_envelope(ogg)
            r = best_lag(flux, frame_ms, np.asarray(s["noteMs"], np.float64), float(s["musicStartMs"]))
        except Exception as e:                                     # 壞檔不要讓整批停下來
            print(f"[warn] {stem}: {e}", file=sys.stderr)
            continue
        if r is None:
            continue
        lag, conf, n = r
        rows.append({"gn": stem, "lagMs": round(lag, 1), "offsetMs": round(-lag, 1),
                     "conf": round(conf, 3), "notes": n, "bpm": s["bpm"]})
        if (i + 1) % 200 == 0:
            print(f"  … {i + 1}/{len(songs)}", file=sys.stderr)

    if not rows:
        print("沒有量到任何一首。", file=sys.stderr)
        return 1

    lags = np.array([r["lagMs"] for r in rows])
    confs = np.array([r["conf"] for r in rows])
    good = confs >= args.min_conf

    print()
    print(f"量到 {len(rows)} 首（沒有音檔 {no_audio} 首）；信心度 >= {args.min_conf} 的有 {good.sum()} 首")
    print()
    print("  lag = 音檔的瞬態比譜面說的晚多少（ms）。要補的 offsetMs = -lag。")
    print()
    for label, v in (("全部", lags), (f"信心度>={args.min_conf}", lags[good])):
        if len(v) == 0:
            continue
        print(f"  [{label}]  n={len(v)}")
        print(f"     中位數 {np.median(v):+7.1f} ms      平均 {v.mean():+7.1f} ms      標準差 {v.std():6.1f} ms")
        qs = np.percentile(v, [5, 25, 50, 75, 95])
        print(f"     百分位  5% {qs[0]:+.0f}   25% {qs[1]:+.0f}   50% {qs[2]:+.0f}   75% {qs[3]:+.0f}   95% {qs[4]:+.0f}")
        print()

    # 直方圖（10ms 一格）——分布長什麼樣，比任何統計量都有說服力
    v = lags[good] if good.sum() > 20 else lags
    lo, hi = -120, 120
    bins = np.arange(lo, hi + 10, 10)
    hist, _ = np.histogram(np.clip(v, lo, hi - 1), bins=bins)
    peak = hist.max() or 1
    print("  分布（信心度過關者，10ms 一格）:")
    for b, c in zip(bins[:-1], hist):
        bar = "#" * int(round(40 * c / peak))
        print(f"   {b:+5d} ~ {b+10:+5d} ms  {c:5d} |{bar}")
    print()

    if args.csv:
        with open(args.csv, "w", newline="", encoding="utf-8-sig") as f:
            w = csv.DictWriter(f, fieldnames=["gn", "lagMs", "offsetMs", "conf", "notes", "bpm"])
            w.writeheader()
            w.writerows(rows)
        print(f"逐首結果 → {args.csv}")

    if not args.apply:
        print("（只量不寫。看過上面的分布、確認它不是系統性偏差之後，再加 --apply）")
        return 0

    # ---- --apply：寫回 song_table.csv 的 offsetMs ----
    # rows 的 gn 是**詞幹**（gn_note_export 的輸出）；表是一個 .gn 一列，所以一個詞幹對到 K/T 兩列。
    # 兩份譜共用同一個音檔 → 同一個 offset，設在 K 列上，st.save 會同步到 T 列。
    table_path = Path(args.table)
    table = st.load(table_path)
    by_stem = {}
    for row in table:
        by_stem.setdefault(st.stem(row["gn"]), []).append(row)

    set_n = skip_conf = skip_abs = skip_missing = 0
    for r in rows:
        targets = by_stem.get(st.stem(r["gn"]))
        if not targets:
            skip_missing += 1
            continue
        if r["conf"] < args.min_conf or r["notes"] < args.min_notes:
            skip_conf += 1
            continue
        if abs(r["lagMs"]) > args.max_abs:      # 超過門檻 = 可能是譜/音檔根本配錯，不要亂設
            skip_abs += 1
            continue
        for row in targets:
            row["offsetMs"] = round(r["offsetMs"], 1)
        set_n += 1

    st.save(table, table_path)
    print(f"寫入 {table_path}")
    print(f"  設定 {set_n} 首；跳過：信心不足/打點太少 {skip_conf}、|lag| > {args.max_abs}ms {skip_abs}、"
          f"表裡沒有這首 {skip_missing}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
