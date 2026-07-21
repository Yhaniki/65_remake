#!/usr/bin/env python3
"""掃全曲庫的「基準速度」(base BPM)：把 ManiaScroll.BaseBeatLength 的規則原樣搬到 Python，
跑過每一份 .gn，列出**與 osu 原版 GetMostCommonBeatLength 不同**的譜面。

用途：改了 ManiaScroll 的門檻常數（FastTempoMinRunMs / FastTempoMinTimeFraction /
FastTempoMinNoteFraction / MaxBaseTempoRatio / TempoClusterTolerance）之後，用這支重產
docs/architecture/scroll-base-bpm.md 的個案表，看看實際被改到哪些歌。

    python tools/scan_base_bpm.py                     # 預設門檻，輸出 markdown 表
    python tools/scan_base_bpm.py --run 8 --frac 0.25 # 試別的門檻
    python tools/scan_base_bpm.py --sensitivity       # 幾組門檻各會改到幾首
    python tools/scan_base_bpm.py --gn sdom5028k      # 單首的速度分群明細

音樂資料夾預設讀 repo 根的 data_root.txt（<root>/MUSIC），可用 --music 覆寫。
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from gn_keytable import STEPFILE_HEADER, lcg_transform, decrypt_ddrm  # noqa: E402

REPO = Path(__file__).resolve().parent.parent
STREAMING = REPO / "65" / "My project" / "Assets" / "StreamingAssets"

# ---- ManiaScroll 的門檻（預設值要和 C# 一致）----
TOL = 0.03            # TempoClusterTolerance
MIN_RUN = 6000.0      # FastTempoMinRunMs
MIN_FRAC = 0.20       # FastTempoMinTimeFraction
MIN_NOTE_FRAC = 0.05  # FastTempoMinNoteFraction
MAX_RATIO = 2.5       # MaxBaseTempoRatio
FAST_MAX_MULT = 1.5   # FastSectionMaxMultiplier（爭議譜：表頭當基準，但快段最多這麼快）


def default_music() -> Path:
    dr = REPO / "data_root.txt"
    if dr.exists():
        root = Path(dr.read_text(encoding="utf-8").strip())
        if (root / "MUSIC").is_dir():
            return root / "MUSIC"
    raise SystemExit("找不到 MUSIC 資料夾，請用 --music 指定")


def plain_body(raw: bytes, e: dict):
    """依 gn_keytable 的項目把 .gn 解成明文（表頭 + StepFrame body）。"""
    enc = e.get("enc")
    if enc == "sdom":
        off = int(e["innerOff"]); inner = raw[off:]
        return inner[:STEPFILE_HEADER] + lcg_transform(e["seed"], inner[STEPFILE_HEADER:], False)
    if enc == "plain":
        return raw
    if enc == "ddrm":
        d = decrypt_ddrm(raw)
        return d[0] if d else None
    if enc == "rewu":
        return lcg_transform(e["seed"], raw, False)
    return None


def scan_difficulty(body: bytes, diff: int):
    """回傳 (BPM 事件 [(beat,bpm)], note 拍點 [beat])。對應 GnChart.Parse 的 pass 1。"""
    addr = struct.unpack_from("<4I", body, 284)
    start = addr[diff]
    end = addr[diff + 1] if diff + 1 < 4 else len(body)
    if not (300 <= start < end <= len(body)):
        start, end = 300, len(body)
    off = start; bpms = []; notes = []
    while off + 8 <= end:
        meas = struct.unpack_from("<I", body, off)[0]
        ft = struct.unpack_from("<h", body, off + 4)[0]
        iv = struct.unpack_from("<H", body, off + 6)[0]
        off += 8
        for i in range(iv):
            if off + 4 > end:
                break
            beat = meas * 4.0 + 4.0 * i / max(1, iv)
            if ft == 1:
                b = struct.unpack_from("<f", body, off)[0]
                if b == b and 1 < b < 100000:
                    bpms.append((beat, b))
            elif ft in (2, 3, 4, 5):
                if struct.unpack_from("<h", body, off)[0] != 0:
                    notes.append(beat)
            off += 4
    return bpms, notes


def build_timeline(header_bpm: float, bpms, notes):
    """拍 → 毫秒（GnChart.BuildBpmTimeline 的等價物）。回傳 (timing points, note ms, 最後一顆 note ms)。"""
    segs = [(0.0, header_bpm)]
    for b, v in sorted(bpms):
        if abs(segs[-1][0] - b) < 1e-9:
            segs[-1] = (b, v)
        elif abs(segs[-1][1] - v) < 1e-9:
            continue
        else:
            segs.append((b, v))
    ms = [0.0]
    for i in range(1, len(segs)):
        ms.append(ms[-1] + (segs[i][0] - segs[i - 1][0]) * 60000.0 / max(1.0, segs[i - 1][1]))

    def b2ms(beat):
        i = 0
        for k in range(len(segs)):
            if segs[k][0] <= beat:
                i = k
        return ms[i] + (beat - segs[i][0]) * 60000.0 / max(1.0, segs[i][1])

    tps = [(ms[i], 60000.0 / max(1.0, segs[i][1])) for i in range(len(segs))]
    note_ms = sorted(b2ms(b) for b in notes)
    return tps, note_ms, (note_ms[-1] if note_ms else (ms[-1] if ms else 0.0))


def same(bl: float, seed: float) -> bool:
    return seed > 0 and abs(bl / seed - 1.0) <= TOL


def base_beat_length(tps, note_ms, last_obj_ms, preferred_bpm=0.0, stats=None):
    """ManiaScroll.BaseBeatLength 的逐行對應版（tps = [(timeMs, beatLength)]，只含 uninherited）。"""
    if stats is None:
        stats = {}
    if not tps:
        return 0.0
    order = sorted(range(len(tps)), key=lambda i: tps[i][0])
    m = len(order)
    end = max(last_obj_ms, tps[order[-1]][0])
    dur = {}; distinct = []; t0s = []; t1s = []
    for k in range(m):
        t0 = 0.0 if k == 0 else tps[order[k]][0]
        t1 = tps[order[k + 1]][0] if k + 1 < m else end
        bl = tps[order[k]][1]
        t0s.append(t0); t1s.append(max(t0, t1))
        dur[bl] = dur.get(bl, 0.0) + max(0.0, t1 - t0)
        if bl not in distinct:
            distinct.append(bl)
    chart = max(1.0, end)
    ntot = len(note_ms) if note_ms else 0

    def group_stats(seed):
        total = sum(dur[bl] for bl in distinct if same(bl, seed))
        longest = 0.0; run_start = 0.0; in_run = False
        for k in range(m):
            member = same(tps[order[k]][1], seed)
            if member and not in_run:
                in_run = True; run_start = t0s[k]
            if in_run and (not member or k == m - 1):
                run_end = t1s[k] if member else t0s[k]
                longest = max(longest, run_end - run_start)
                in_run = member and k < m - 1
        notes = 0
        if ntot:
            for k in range(m):
                if not same(tps[order[k]][1], seed):
                    continue
                for t in note_ms:
                    if t >= t0s[k] and (t < t1s[k] or k == m - 1):
                        notes += 1
        return total, longest, notes

    mc_seed = distinct[0]; mc_total = -1.0; mc_seed_dur = -1.0
    qualified = []
    for seed in distinct:
        total, longest, notes = group_stats(seed)
        seed_dur = dur[seed]
        if total > mc_total + 1e-9 or (total > mc_total - 1e-9 and seed_dur > mc_seed_dur):
            mc_total, mc_seed_dur, mc_seed = total, seed_dur, seed
        long_enough = longest >= MIN_RUN or total / chart >= MIN_FRAC
        has_notes = ntot == 0 or notes / ntot >= MIN_NOTE_FRAC
        if long_enough and has_notes:
            qualified.append(seed)

    qualified.sort()
    winner = mc_seed
    for q in qualified:
        if q >= mc_seed:
            break
        if mc_seed / q <= MAX_RATIO + 1e-9:
            winner = q
            break

    total, longest, notes = group_stats(winner)
    stats.update(run=longest, frac=total / chart, notes=notes, ntot=ntot,
                 contested=winner != mc_seed)

    def representative(seed):
        if preferred_bpm > 0:
            pb = 60000.0 / preferred_bpm
            if same(pb, seed):
                return pb
        best = seed; best_dur = -1.0
        for bl in distinct:
            if not same(bl, seed):
                continue
            if dur[bl] > best_dur:
                best_dur, best = dur[bl], bl
        return best

    winner_beat = representative(winner)
    if winner == mc_seed:
        return winner_beat

    # 爭議譜：基準用選歌畫面顯示的 BPM（表頭），夾在兩群之間，再保證快段 ≤ FAST_MAX_MULT×
    hi = 60000.0 / winner_beat
    lo = 60000.0 / representative(mc_seed)
    bpm = min(max(preferred_bpm, lo), hi) if preferred_bpm > 0 else lo
    bpm = min(max(bpm, hi / FAST_MAX_MULT), hi)
    stats.update(lo=lo, hi=hi)
    return 60000.0 / bpm


def osu_most_common(tps, last_obj_ms):
    """osu 原版 GetMostCommonBeatLength（逐一 exact match，不併群）。"""
    order = sorted(range(len(tps)), key=lambda i: tps[i][0])
    m = len(order); end = max(last_obj_ms, tps[order[-1]][0]); dur = {}
    for k in range(m):
        t0 = 0.0 if k == 0 else tps[order[k]][0]
        t1 = tps[order[k + 1]][0] if k + 1 < m else end
        bl = tps[order[k]][1]
        dur[bl] = dur.get(bl, 0.0) + max(0.0, t1 - t0)
    return max(dur.items(), key=lambda kv: kv[1])[0]


def load_charts(music: Path, diff: int):
    kt = json.load(open(STREAMING / "gn_keytable.json", encoding="utf-8"))["songs"]
    out = []
    for e in kt:
        p = music / e["gn"]
        if not p.exists():
            alt = music / e["gn"].replace("k.gn", "K.gn").replace("t.gn", "T.gn")
            if not alt.exists():
                continue
            p = alt
        try:
            body = plain_body(p.read_bytes(), e)
            if not body or len(body) < 300:
                continue
            hb = struct.unpack_from("<f", body, 16)[0]
            if not (1 < hb < 1000):
                continue
            bpms, notes = scan_difficulty(body, diff)
            if not bpms:
                continue          # 單一 BPM：base 沒有歧義
            tps, note_ms, last = build_timeline(hb, bpms, notes)
            out.append((e["gn"], hb, tps, note_ms, last))
        except Exception as ex:
            print(f"ERR {e['gn']}: {ex}", file=sys.stderr)
    return out


def titles():
    t = {}
    for s in json.load(open(STREAMING / "song_catalog.json", encoding="utf-8"))["songs"]:
        t.setdefault(s["gn"].lower().replace("t.gn", "").replace("k.gn", ""), s["title"])
    return t


def main() -> int:
    global MIN_RUN, MIN_FRAC, MIN_NOTE_FRAC, MAX_RATIO, TOL
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--music", type=Path, default=None)
    ap.add_argument("--difficulty", type=int, default=2, help="0=easy 1=normal 2=hard(預設)")
    ap.add_argument("--run", type=float, default=MIN_RUN / 1000, help="最長連續門檻(秒)")
    ap.add_argument("--frac", type=float, default=MIN_FRAC, help="累計時長門檻(0-1)")
    ap.add_argument("--note-frac", type=float, default=MIN_NOTE_FRAC, help="note 佔比門檻(0-1)")
    ap.add_argument("--ratio", type=float, default=MAX_RATIO, help="基準對最多群的倍率上限")
    ap.add_argument("--tol", type=float, default=TOL, help="併群容差(0-1)")
    ap.add_argument("--sensitivity", action="store_true", help="幾組門檻各會改到幾首")
    ap.add_argument("--gn", default=None, help="只看某一首的分群明細（詞幹或檔名）")
    args = ap.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    MIN_RUN, MIN_FRAC, MIN_NOTE_FRAC, MAX_RATIO, TOL = (
        args.run * 1000, args.frac, args.note_frac, args.ratio, args.tol)
    music = args.music or default_music()
    charts = load_charts(music, args.difficulty)

    if args.gn:
        key = args.gn.lower().replace(".gn", "")
        for gn, hb, tps, note_ms, last in charts:
            if key not in gn.lower():
                continue
            st = {}
            new = 60000.0 / base_beat_length(tps, note_ms, last, hb, st)
            old = 60000.0 / osu_most_common(tps, last)
            print(f"{gn}  表頭 {hb:.4g}  osu {old:.5g} → 新規則 {new:.5g}")
            print(f"  勝出群：最長連續 {st['run']/1000:.1f}s，累計 {st['frac']:.0%}，"
                  f"note {st['notes']}/{st['ntot']}")
            segs = sorted({round(60000.0 / bl, 4) for _, bl in tps})
            print("  出現過的 BPM：" + ", ".join(f"{b:g}" for b in reversed(segs)))
        return 0

    if args.sensitivity:
        print(f"語料：{len(charts)} 首有 BPM 變化的譜（{music}）")
        for run, frac in [(4, 0.15), (6, 0.20), (8, 0.25), (10, 0.30), (1e12, 0.20), (6, 1.1)]:
            MIN_RUN, MIN_FRAC = run * 1000, frac
            n = sum(1 for gn, hb, tps, nms, last in charts
                    if abs(osu_most_common(tps, last) / base_beat_length(tps, nms, last, hb) - 1) > 0.03)
            lbl = f"連續≥{run:g}s 或 累計≥{frac:.0%}" if run < 1e9 else f"只看累計≥{frac:.0%}"
            print(f"  {lbl:28s} → {n} 首與 osu 原版不同")
        return 0

    tt = titles()
    rows = []
    for gn, hb, tps, note_ms, last in charts:
        st = {}
        new = base_beat_length(tps, note_ms, last, hb, st)
        old = osu_most_common(tps, last)
        if abs(old / new - 1) > 0.03:
            rows.append((gn, hb, 60000.0 / old, 60000.0 / new, st))
    print(f"多 BPM 譜 {len(charts)} 首；與 osu 原版不同的 {len(rows)} 首")
    print("| gn | 歌名 | 表頭 | osu 舊基準 | 新基準 | 快段× | 慢段× | 判定 |")
    print("|---|---|---|---|---|---|---|---|")
    for gn, hb, old, new, st in sorted(rows, key=lambda r: -r[3] / r[2]):
        stem = gn.lower().replace("k.gn", "").replace("t.gn", "")
        if st.get("contested") and st["hi"] > st["lo"] * 1.001:
            hi, lo = st["hi"], st["lo"]
            where = ("表頭＝快群" if new >= hi * 0.999 else
                     "表頭落中間" if new <= hb * 1.001 else "表頭＝慢群→抬到快段1.5×")
            note = f'爭議 {lo:.4g}／{hi:.4g}，{where}'
        else:
            nf = st["notes"] / st["ntot"] if st["ntot"] else 0
            note = f'單群：連續{st["run"]/1000:.1f}s，累計{st["frac"]:.0%}，note {nf:.0%}'
            hi = lo = new
        print(f'| {gn.replace(".gn","")} | {tt.get(stem,"?")} | {hb:.5g} | {old:.5g} | **{new:.5g}** | '
              f'{hi/new:.2f} | {lo/new:.2f} | {note} |')
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
