#!/usr/bin/env python3
"""ch0 keyframes (FLOAT-encoded) + word[0x1f4] velocity multiplier per emitter. The engine UPDATE integrates
pos += vel * param_1[0x1f4] * ch0Scale(t)  (decompiled FUN_004bd570 ~136220). I had DROPPED word[0x1f4] from
velocity (thought it was size-jitter) — but it scales velocity. tex96 0.61x-too-short = its word[0x1f4]>1 missing."""
import struct, sys

TEMPLATE, STRIDE = 0x8004, 0x8c8

def emitter(slot): return TEMPLATE + slot * STRIDE

def wf(d, eb, w): return struct.unpack_from('<f', d, eb + w*4)[0]
def wi(d, eb, w): return struct.unpack_from('<i', d, eb + w*4)[0]

def channel(d, eb, ch):
    cb = eb + (ch + 9) * 0x13 * 4
    cnt = struct.unpack_from('<i', d, cb)[0]
    mn = struct.unpack_from('<f', d, cb + 0x11*4)[0]
    mx = struct.unpack_from('<f', d, cb + 0x12*4)[0]
    kf = [(struct.unpack_from('<f', d, cb + (1+2*k)*4)[0] / 480.0,
           1.0 - struct.unpack_from('<f', d, cb + (2+2*k)*4)[0] / 462.0) for k in range(cnt)]
    return cnt, mn, mx, kf

def decoded(kf, cnt, t):                  # engine FUN_004bcd30: lerp in-segment, 0 outside [t0,tlast)
    if cnt < 2: return 0.0
    for k in range(cnt-1):
        t0, v0 = kf[k]; t1, v1 = kf[k+1]
        if t0 <= t < t1: return v0 + (v1-v0)*(t-t0)/(t1-t0)
    return kf[cnt-1][1] if t >= kf[cnt-1][0] else kf[0][1]   # my clamp (engine returns 0 here)

def scale(v, mn, mx):
    half = (mx-mn)*0.5
    if v < 0.5:
        dd = (0.5-v)*half + 1.0; return 1.0/dd if dd else v
    return (v-0.5)*half + 1.0

def report(path, slot, label):
    d = open(path,'rb').read(); eb = emitter(slot)
    cnt, mn, mx, kf = channel(d, eb, 0)
    s = wf(d, eb, 0x1f4)
    vy = wf(d, eb, 0x15)
    print(f"\n== {label}: slot{slot} ==  word[0x1f4]={s:.3f}  vel.y={vy:.4f}  ch0 cnt={cnt} min={mn:.1f} max={mx:.1f}")
    print(f"   keyframes: " + "  ".join(f"(t={t:.3f},v={v:.3f})" for t,v in kf))
    print( "   t      ch0Scale   ×word[0x1f4]")
    for t in (0.05, 0.3, 0.6, 0.9):
        cs = scale(decoded(kf,cnt,t), mn, mx)
        print(f"   {t:.2f}   {cs:7.3f}    {cs*s:7.3f}")

if __name__ == '__main__':
    b = 'assets/sdox_offline/Extracted/3DEFT'
    report(f'{b}/200COMBO.EFT', 2, 'tex31 orbs (GOOD 90%)')
    report(f'{b}/300COMBO.EFT', 1, 'tex30 orbs (300)')
    report(f'{b}/400COMBO.EFT', 3, 'tex96 riser life100 (BAD 61%)')
    report(f'{b}/400COMBO.EFT', 4, 'tex96 life50 (400)')
    report(f'{b}/FINISHED.EFT', 1, 'tex100 naga (FINISHED)')
    report(f'{b}/FINISHED.EFT', 4, 'tex96 (FINISHED)')
