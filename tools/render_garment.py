# -*- coding: utf-8 -*-
"""Tiny software rasterizer for one SDO garment MSH (bind pose, front ortho view).
Draws the attribute ranges in order with z-test + straight alpha blend, so we can see what the
official 2-layer composite actually looks like, and toggle a layer off for comparison."""
import struct, sys, os
import numpy as np
from PIL import Image

DIR = "H:/65_remake_clean/DATA/AVATAR/"
W = H = 420


def u32(d, p): return struct.unpack_from("<I", d, p)[0]


def cstr(d, o, mx):
    n = 0
    while n < mx and o + n < len(d) and d[o + n] != 0:
        n += 1
    return d[o:o + n].decode('latin1')


def parse(path):
    d = open(path, 'rb').read()
    p = 12
    n = u32(d, p); p += 4
    p += 4
    idxSize = u32(d, p); p += 8
    idx = np.frombuffer(d, dtype='<u2', count=idxSize // 2, offset=p).astype(np.int32)
    p += idxSize
    vertSize = u32(d, p); p += 4
    stride = u32(d, p); p += 4
    vcount = vertSize // stride
    vo = p
    V = np.array([struct.unpack_from("<3f", d, vo + i * stride) for i in range(vcount)])
    UV = np.array([struct.unpack_from("<2f", d, vo + i * stride + stride - 8) for i in range(vcount)])
    p += vertSize + 24
    numMat = u32(d, p); p += 4
    firstMat = p
    names = []
    for m in range(numMat):
        names.append(cstr(d, firstMat + m * 408 + 68, 320))
    p = firstMat + numMat * 408
    tri = len(idx) // 3
    # range table
    off = p
    ranges = []
    while off < min(len(d), p + 0x300) - 8:
        cnt = u32(d, off)
        if 0 < cnt <= 32 and off + 4 + cnt * 24 <= len(d):
            tmp = []
            ok = True
            fsum = 0
            prev = -1
            for i in range(cnt):
                o = off + 4 + i * 24
                a, fs, fc, vs, vc = struct.unpack_from("<5I", d, o)
                if fc == 0 or fs < prev or fs + fc > tri or vs + vc > vcount:
                    ok = False; break
                prev = fs; fsum += fc; tmp.append((a, fs, fc))
            if ok and fsum == tri and all(t[0] < max(1, numMat) for t in tmp):
                ranges = tmp; break
        off += 4
    return V, UV, idx.reshape(-1, 3), names, ranges


def tex(name):
    return np.array(Image.open(DIR + name.upper()).convert('RGBA')).astype(np.float32) / 255.0


def render(path, skip_ranges=(), out="out.png"):
    V, UV, T, names, ranges = parse(path)
    lo, hi = V.min(0), V.max(0)
    c = (lo + hi) / 2
    span = (hi - lo).max() * 0.6
    # screen: x right, y up
    sx = (V[:, 0] - c[0]) / span * W + W / 2
    sy = H / 2 - (V[:, 1] - c[1]) / span * H
    sz = V[:, 2]
    img = np.zeros((H, W, 3), np.float32) + 0.15   # dark grey bg
    zb = np.full((H, W), 1e9, np.float32)
    for ri, (a, fs, fc) in enumerate(ranges):
        if ri in skip_ranges:
            continue
        tx = tex(names[a])
        th, tw = tx.shape[:2]
        for f in range(fs, fs + fc):
            i0, i1, i2 = T[f]
            x = sx[[i0, i1, i2]]; y = sy[[i0, i1, i2]]; z = sz[[i0, i1, i2]]
            uvs = UV[[i0, i1, i2]]
            xmin, xmax = int(max(0, np.floor(x.min()))), int(min(W - 1, np.ceil(x.max())))
            ymin, ymax = int(max(0, np.floor(y.min()))), int(min(H - 1, np.ceil(y.max())))
            if xmax < xmin or ymax < ymin:
                continue
            xs, ys = np.meshgrid(np.arange(xmin, xmax + 1), np.arange(ymin, ymax + 1))
            d = ((y[1] - y[2]) * (x[0] - x[2]) + (x[2] - x[1]) * (y[0] - y[2]))
            if abs(d) < 1e-9:
                continue
            l0 = ((y[1] - y[2]) * (xs - x[2]) + (x[2] - x[1]) * (ys - y[2])) / d
            l1 = ((y[2] - y[0]) * (xs - x[2]) + (x[0] - x[2]) * (ys - y[2])) / d
            l2 = 1 - l0 - l1
            m = (l0 >= 0) & (l1 >= 0) & (l2 >= 0)
            if not m.any():
                continue
            zz = l0 * z[0] + l1 * z[1] + l2 * z[2]
            u = l0 * uvs[0, 0] + l1 * uvs[1, 0] + l2 * uvs[2, 0]
            v = l0 * uvs[0, 1] + l1 * uvs[1, 1] + l2 * uvs[2, 1]
            px = np.clip((u % 1.0 * tw).astype(int), 0, tw - 1)
            py = np.clip((v % 1.0 * th).astype(int), 0, th - 1)
            src = tx[py, px]
            sub = zb[ymin:ymax + 1, xmin:xmax + 1]
            m &= (zz <= sub)
            if not m.any():
                continue
            al = (src[..., 3] * m).astype(np.float32)
            dst = img[ymin:ymax + 1, xmin:xmax + 1]
            img[ymin:ymax + 1, xmin:xmax + 1] = dst * (1 - al[..., None]) + src[..., :3] * al[..., None]
            sub[m] = zz[m]
    Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).save(out)
    print(out, 'ranges', ranges, 'names', names, 'skipped', skip_ranges)


if __name__ == "__main__":
    msh = sys.argv[1]
    skip = tuple(int(x) for x in sys.argv[2].split(',') if x != '') if len(sys.argv) > 2 else ()
    render(DIR + msh, skip, sys.argv[3] if len(sys.argv) > 3 else "out.png")
