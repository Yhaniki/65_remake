#!/usr/bin/env python3
r"""Scan COMBO EFT slots for 3D mesh-attach references (xmesh\list.txt index), not just texture index.
The particle mesh-attach field is param_1[6] in the draw; flag 0x40000 = mesh carrier (skips billboard draw).
xmesh idx 32 = adol_x\aef03_00 (AEF03_00.MSH/.HRC textured with AEF_3_00.DDS)."""
import struct
TEMPLATE, STRIDE = 0x8004, 0x8c8
def sb(s): return TEMPLATE + s * STRIDE
def wi(d, b, w): return struct.unpack_from('<i', d, b + w * 4)[0]

XLIST = 'assets/sdox_offline/Extracted/3DEFT/XMESH/LIST.TXT'
BS = chr(92)  # backslash

xm = {}
for ln in open(XLIST, 'rb').read().split(b'\n'):
    if len(ln) > 4 and ln[3:4] == b'|':
        try:
            idx = int(ln[:3])
        except ValueError:
            continue
        toks = ln[4:].split()
        nm = ''.join(chr(c) for c in toks[1] if 32 <= c < 127) if len(toks) > 1 else ''
        low = nm.lower().rstrip(BS)
        if nm and low != 'xmesh':
            xm[idx] = nm

out = ["xmesh named indices: " + str({k: v for k, v in sorted(xm.items())}), ""]
for fn in ['100COMBO', '200COMBO', '300COMBO', '400COMBO', '500COMBO']:
    d = open('assets/Datas/3DEFT/%s.EFT' % fn, 'rb').read()
    n = (len(d) - TEMPLATE) // STRIDE
    out.append("=== %s ===" % fn)
    for s in range(min(n, 10)):
        b = sb(s)
        flags = wi(d, b, 1) & 0xffffffff
        tex = wi(d, b, 0x47)
        w6 = wi(d, b, 6)
        meshhit = []
        for w in range(2, 0x20):
            v = wi(d, b, w)
            if v in xm:
                meshhit.append("w%s=%d(%s)" % (hex(w), v, xm[v]))
        mh = '  MESH:' + '; '.join(meshhit) if meshhit else ''
        ext = ' [0x40000 carrier]' if flags & 0x40000 else ''
        out.append(" slot%d: flags=0x%08x tex=%d word6=%d%s%s" % (s, flags, tex, w6, ext, mh))

open('mesh_scan_tmp.txt', 'w', encoding='utf-8').write('\n'.join(out))
print("written")
