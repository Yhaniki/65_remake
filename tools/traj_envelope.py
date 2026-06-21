#!/usr/bin/env python3
"""Extract per-emitter world-reach envelopes from a Frida/sim trajectory log so the
official capture (eft_online_log.txt) and my sim dump (unity-traj.log) can be compared.

Groups `traj <kind> texN#n t=a/L pos=(x,y,z)` lines by (section, tex). A section starts
at any header line containing one of: 'NEW EFFECT', 'CAPTURE START', 'MYSIM COMBO'.
For each (section,tex) prints particle count, max|x|, maxY, max|z|, and the mean final pos.
"""
import re, sys, collections

PAT = re.compile(r'traj\s+(\w+)\s+tex(\d+)#(\d+)\s+t=(\d+)/(\d+)\s+pos=\(([-\d.]+),([-\d.]+),([-\d.]+)\)')
HDR = re.compile(r'(NEW EFFECT.*|CAPTURE START.*|MYSIM COMBO \d+|FINISHED)')

def parse(path):
    section = "?"
    # (section,tex) -> {n:set, maxx,maxy,maxz, finals:{pid:(t,x,y,z)}}
    data = collections.OrderedDict()
    with open(path, encoding='utf-8', errors='replace') as f:
        for line in f:
            h = HDR.search(line)
            if h and 'traj' not in line:
                section = h.group(1).strip()[:40]
                continue
            m = PAT.search(line)
            if not m:
                continue
            kind, tex, pid, t, L, x, y, z = m.groups()
            tex, pid, t = int(tex), int(pid), int(t)
            x, y, z = float(x), float(y), float(z)
            key = (section, tex, kind)
            d = data.setdefault(key, {'ids': set(), 'mx': 0, 'my': -1e9, 'myn': 1e9,
                                      'mz': 0, 'finals': {}})
            d['ids'].add(pid)
            d['mx'] = max(d['mx'], abs(x)); d['mz'] = max(d['mz'], abs(z))
            d['my'] = max(d['my'], y); d['myn'] = min(d['myn'], y)
            ft, *_ = d['finals'].get(pid, (-1, 0, 0, 0))
            if t >= ft:
                d['finals'][pid] = (t, x, y, z)
    return data

def report(path):
    data = parse(path)
    print(f"\n###### {path} ######")
    cur = None
    for (section, tex, kind), d in data.items():
        if section != cur:
            cur = section; print(f"\n--- {section} ---")
        fx = sum(v[1] for v in d['finals'].values()) / len(d['finals'])
        fy = sum(v[2] for v in d['finals'].values()) / len(d['finals'])
        fz = sum(v[3] for v in d['finals'].values()) / len(d['finals'])
        print(f"  tex{tex:<3} {kind:<5} n={len(d['ids']):<3} "
              f"max|x|={d['mx']:6.1f} Y[{d['myn']:6.1f}..{d['my']:6.1f}] max|z|={d['mz']:6.1f} "
              f"| meanFinal=({fx:6.1f},{fy:6.1f},{fz:6.1f})")

if __name__ == '__main__':
    for p in sys.argv[1:]:
        report(p)
