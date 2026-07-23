# -*- coding: utf-8 -*-
"""
解析「解密後」的 .nx 譜面 blob（dump_chart.py 存下來的 dumped_*.bin），印出：
  表頭 / 每難度的 frame_type·note_type 統計 / 捲動速度表(frame_type 33) / 炸彈表(note_type 1)

也吃「明文 StepFile」（開頭就是 file_id + 'gn\\0\\0'）。格式細節見 NX_FORMAT.md。

用法：
    python decode_chart.py dumped_seedbc9051fd_len1384076.bin
    python decode_chart.py dumped.bin --diff hard      # 只看單一難度
"""
from __future__ import annotations
import argparse, struct, sys
from collections import Counter, defaultdict

DIFFS = ("easy", "normal", "hard")
LANE = {2: "Left", 3: "Up", 4: "Down", 5: "Right"}
FT_NAME = {1: "BPM變化", 9: "小節線", 10: "音樂起止", 11: "結束標記", 33: "捲動速度"}
NT_NAME = {0: "一般音符", 1: "炸彈", 2: "長條頭", 3: "長條尾"}
SCROLL_BASE = 1000.0          # 1000 = 原速 ×1.0
RAMP_TICKS_PER_BEAT = 48.0    # 高16位時長單位 = 1/48 拍


def _utf8_stdout():
    """Windows 主控台預設 cp950/cp437 會把中文印成亂碼 —— 強制 UTF-8。"""
    out = getattr(sys, "stdout", None)
    if out is not None and hasattr(out, "reconfigure"):
        try: out.reconfigure(encoding="utf-8", errors="replace")
        except Exception: pass


def u32(b, o): return struct.unpack_from("<I", b, o)[0]
def i16(b, o): return struct.unpack_from("<h", b, o)[0]
def u16(b, o): return struct.unpack_from("<H", b, o)[0]
def f32(b, o): return struct.unpack_from("<f", b, o)[0]
def cstr(b, o, n=32): return b[o:o + n].split(b"\0", 1)[0].decode("utf-8", "replace")


def frames(b, start, end):
    """逐一 yield (measurement, frame_type, interval, [(slot_index, raw_u32)...])。"""
    off = start
    while off + 8 <= end:
        meas, ft, iv = u32(b, off), i16(b, off + 4), u16(b, off + 6)
        off += 8
        if iv > 20000 or off + iv * 4 > end:
            return
        yield meas, ft, iv, [(i, u32(b, off + 4 * i)) for i in range(iv)]
        off += iv * 4


def beat_in_measure(i, iv):
    """編輯器顯示的小節內拍（1.0 ~ 4.x）。"""
    return round(1 + (i / max(1, iv)) * 4, 3)


def main() -> int:
    _utf8_stdout()
    ap = argparse.ArgumentParser(description="解析解密後的 .nx 譜面")
    ap.add_argument("path")
    ap.add_argument("--diff", choices=DIFFS, help="只看單一難度")
    a = ap.parse_args()

    b = open(a.path, "rb").read()
    if b[4:6] != b"gn":
        print("不像明文 StepFile（offset 4 不是 'gn'）——這檔可能還沒解密。", file=sys.stderr)
        return 1

    addr = [u32(b, 284 + 4 * i) for i in range(4)]
    print(f"檔案 {a.path}  ({len(b)} bytes)")
    print(f"  file_id={u32(b,0)}  BPM={f32(b,16)}  levels={[i16(b,20+2*i) for i in range(3)]}")
    print(f"  note_count={[u32(b,40+4*i) for i in range(3)]}（含炸彈）  measurements={[u32(b,64+4*i) for i in range(3)]}")
    print(f"  title={cstr(b,108)!r} artist={cstr(b,172)!r} producer={cstr(b,204)!r}")
    print(f"  address={addr}   note 資料到 {addr[3]}，之後是 dps/png/ogg")

    for d, name in enumerate(DIFFS):
        if a.diff and name != a.diff:
            continue
        s, e = addr[d], addr[d + 1]
        if not (0 < s < e <= len(b)):
            print(f"\n== {name}: address 不合理 [{s}:{e}]，跳過")
            continue
        ft_hist, nt_hist = Counter(), Counter()
        scroll, bombs = [], []
        for meas, ft, iv, slots in frames(b, s, e):
            ft_hist[ft] += 1
            for i, v in slots:
                if v == 0:
                    continue
                if ft in (2, 3, 4, 5):
                    nt = (v >> 24) & 0xFF
                    nt_hist[nt] += 1
                    if nt == 1:
                        bombs.append((meas, beat_in_measure(i, iv), LANE.get(ft, ft)))
                elif ft == 33:
                    lo, hi = v & 0xFFFF, (v >> 16) & 0xFFFF
                    scroll.append((meas, beat_in_measure(i, iv), lo, lo / SCROLL_BASE, hi))

        print(f"\n===== {name}  [{s}:{e}] =====")
        def ft_label(k):
            return FT_NAME.get(k) or (LANE[k] + "軌" if k in LANE else "事件")
        print("  frame_type:", {k: f"{ft_label(k)}×{v}" for k, v in sorted(ft_hist.items())})
        print("  note_type :", {NT_NAME.get(k, k): v for k, v in sorted(nt_hist.items())})

        if scroll:
            print(f"  -- 捲動速度 frame_type 33（{len(scroll)} 個；base {SCROLL_BASE:.0f} = ×1.0）--")
            by_m = defaultdict(list)
            for meas, beat, raw, mul, ramp in scroll:
                tag = f"b{beat}={raw}(×{mul:g})"
                if ramp:
                    tag += f"[線性{ramp / RAMP_TICKS_PER_BEAT:g}拍]"
                by_m[meas].append(tag)
            for m in sorted(by_m):
                print(f"     小節{m:>3}: " + "  ".join(by_m[m]))
            ramps = [(m, be, mul, r / RAMP_TICKS_PER_BEAT) for m, be, _, mul, r in scroll if r]
            print(f"     其中線性變速 {len(ramps)} 個：" +
                  ", ".join(f"小節{m} b{be}→×{mul:g}({d:g}拍)" for m, be, mul, d in ramps) if ramps
                  else "     （全部都是瞬間切換）")

        if bombs:
            print(f"  -- 炸彈 note_type 1（{len(bombs)} 顆）--")
            by_m = defaultdict(list)
            for meas, beat, lane in bombs:
                by_m[meas].append(f"b{beat}/{lane}")
            for m in sorted(by_m):
                print(f"     小節{m:>3}: " + "  ".join(by_m[m]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
