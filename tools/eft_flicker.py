#!/usr/bin/env python3
"""Dump the 'flicker'/turbulence + alpha-cutoff config for each live emitter of an .EFT.

From the decompiled UPDATE (FUN_004bd570 region, sdo_stand_alone.exe.c ~136145):
  - alpha cutoff: if (alphaChannelVal - word[0x22c]) < 0  -> alpha = 0  (hard pop-out below threshold)
  - velocity turbulence block (~136174), gated by:
        word[0x1fd] != 0  AND  word[0x1fc] != -1  AND  (life & word[0x1fd]) != 0  AND  rand()%100 < word[0x1fb]
    then rotates the velocity by random angles  rand(word[0x1fe],word[0x201]) / (..0x1ff,0x202) / (..0x200,0x203),
    each clamped to +/- word[0x204..0x206]; if word[0x1fc]==1 it self-disables (one-shot).
  - channels 8/9/10 = angular velocity: FUN_004bd4d0(vel, ch8, ch9, ch10) rotates the velocity every frame.
Run: python eft_flicker.py <file.eft> [...]
"""
import struct, sys

TEMPLATE, STRIDE, ROOT, SLOTS = 0x8004, 0x8c8, 0x19904, 32

def words(d, base, n): return struct.unpack_from('<%di' % n, d, base)

def chan(d, eb, ch):
    cb = eb + (ch + 9) * 0x13 * 4
    cnt = struct.unpack_from('<i', d, cb)[0]
    mn  = struct.unpack_from('<f', d, cb + 0x11 * 4)[0]
    mx  = struct.unpack_from('<f', d, cb + 0x12 * 4)[0]
    return cnt, mn, mx

def dump(path):
    d = open(path, 'rb').read()
    print(f"\n###### {path}  ({len(d)} bytes) ######")
    for slot in range(SLOTS):
        eb = TEMPLATE + slot * STRIDE
        if eb + STRIDE > len(d): break
        flags = struct.unpack_from('<i', d, eb + 1*4)[0]
        if flags == 0: continue
        texsel = struct.unpack_from('<i', d, eb + 0x46*4)[0]
        tex = struct.unpack_from('<i', d, eb + (0x47+texsel)*4)[0]
        def wi(w): return struct.unpack_from('<i', d, eb + w*4)[0]
        def wf(w): return struct.unpack_from('<f', d, eb + w*4)[0]
        prob, mode, mask = wi(0x1fb), wi(0x1fc), wi(0x1fd)
        cutoff = wf(0x22c)
        # angVel channels 8/9/10
        av = [chan(d, eb, c) for c in (8, 9, 10)]
        avnz = any(cnt >= 2 and (abs(mn) > 1e-6 or abs(mx) > 1e-6) for cnt, mn, mx in av)
        flick = (mask != 0 and mode != -1)
        tag = []
        if flick: tag.append("TURBULENCE")
        if avnz:  tag.append("ANGVEL")
        if cutoff != 0: tag.append(f"alphaCut={cutoff:.3f}")
        print(f" slot{slot:<2} tex{tex:<3} flags=0x{flags&0xffffffff:05X}  {' '.join(tag) if tag else '-'}")
        if flick:
            print(f"      prob(0x1fb)={prob} mode(0x1fc)={mode} lifeMask(0x1fd)=0x{mask&0xffffffff:X} "
                  f"rndA(0x1fe..200)=[{wf(0x1fe):.3f},{wf(0x1ff):.3f},{wf(0x200):.3f}] "
                  f"rndB(0x201..203)=[{wf(0x201):.3f},{wf(0x202):.3f},{wf(0x203):.3f}] "
                  f"clamp(0x204..206)=[{wf(0x204):.3f},{wf(0x205):.3f},{wf(0x206):.3f}]")
        if avnz:
            print(f"      angVel ch8/9/10 = " +
                  " ".join(f"[cnt{c} {mn:.3f}..{mx:.3f}]" for c, mn, mx in av))

if __name__ == '__main__':
    for p in sys.argv[1:]:
        dump(p)
