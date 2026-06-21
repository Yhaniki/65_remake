#!/usr/bin/env python
"""Parse a ProcMon .pml capture and report which audio files the game opened.

Usage: python pm_analyze.py <capture.pml> [highlight_substring]
  highlight_substring: optional, e.g. "SE_0031.wav" -> reports its open events.
"""
import sys, datetime
from collections import OrderedDict
from procmon_parser import ProcmonLogsReader

BS = chr(92)
def base(p): return p.split(BS)[-1]
def ft(x):
    try:
        return datetime.datetime.fromtimestamp(x/1e7 - 11644473600).strftime("%H:%M:%S.%f")[:-3]
    except Exception:
        return str(x)
def res_name(res):
    r = res & 0xffffffff
    if r == 0:          return "OK"
    if r in (0xC0000034, 0xC000003A): return "NOT_FOUND"
    return hex(r)

pml = sys.argv[1] if len(sys.argv) > 1 else "pm_capture.pml"
hi  = (sys.argv[2].lower() if len(sys.argv) > 2 else "")

total = 0
sounds = []          # (filetime, op, path, result)
scene  = []          # scene/stage asset accesses (timing anchor)
with open(pml, "rb") as f:
    for e in ProcmonLogsReader(f):
        total += 1
        p = (e.path or ""); pl = p.lower()
        if pl.endswith(".wav") or pl.endswith(".ogg"):
            sounds.append((e.date_filetime, e.operation, p, e.result))
        if e.operation == "CreateFile" and any(k in pl for k in
                ("haidi", "scn00", BS + "scene" + BS, "mapobj")):
            scene.append((e.date_filetime, p))

uniq = OrderedDict()
for x, op, p, res in sounds:
    n = base(p)
    if n not in uniq:
        uniq[n] = (x, op, res)

print(f"TOTAL events: {total}   sound events: {len(sounds)}   unique sound files: {len(uniq)}")
print("\n=== UNIQUE sound files opened (chronological by first open) ===")
print("  TIME          OP           RESULT      FILE")
for n, (x, op, res) in sorted(uniq.items(), key=lambda kv: kv[1][0]):
    print("  %s  %-11s  %-9s  %s" % (ft(x), op, res_name(res), n))

if hi:
    hh = [s for s in sounds if hi in s[2].lower()]
    flag = "*** PLAYED ***" if hh else "NOT opened (0 events)"
    print(f"\n=== HIGHLIGHT '{hi}': {len(hh)} event(s)  -> {flag} ===")
    for x, op, p, res in hh[:20]:
        print("  %s %s %s" % (ft(x), op, res_name(res)))

if scene:
    print("\n=== scene/stage asset access (timing anchor, unique) ===")
    seen = set()
    for x, p in scene:
        k = base(p)
        if k in seen:
            continue
        seen.add(k)
        print("  %s  %s" % (ft(x), p))
        if len(seen) >= 10:
            break
