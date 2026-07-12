# -*- coding: utf-8 -*-
# Minimal PMX parser: bones + rigid bodies, to validate cloth-chain extraction.
import struct, sys, io

class R:
    def __init__(self, d): self.d = d; self.p = 0
    def u8(self):  v = self.d[self.p]; self.p += 1; return v
    def i32(self): v = struct.unpack_from('<i', self.d, self.p)[0]; self.p += 4; return v
    def u16(self): v = struct.unpack_from('<H', self.d, self.p)[0]; self.p += 2; return v
    def f(self):   v = struct.unpack_from('<f', self.d, self.p)[0]; self.p += 4; return v
    def v3(self):  return (self.f(), self.f(), self.f())
    def skip(self, n): self.p += n

def idx(r, size, signed=True):
    if size == 1: v = struct.unpack_from('<b' if signed else '<B', r.d, r.p)[0]; r.p += 1
    elif size == 2: v = struct.unpack_from('<h' if signed else '<H', r.d, r.p)[0]; r.p += 2
    else: v = struct.unpack_from('<i', r.d, r.p)[0]; r.p += 4
    return v

def parse(path):
    d = open(path, 'rb').read()
    r = R(d)
    assert d[:4] == b'PMX ', 'bad magic'
    r.skip(4)
    ver = r.f()
    n = r.u8(); g = [r.u8() for _ in range(n)]
    enc, extraUv, vI, tI, mI, bI, mfI, rI = g[:8]
    def text():
        ln = r.i32(); s = d[r.p:r.p+ln]; r.p += ln
        return s.decode('utf-16-le' if enc == 0 else 'utf-8', errors='replace')
    nameJp = text(); nameEn = text(); text(); text()
    # vertices
    vc = r.i32()
    minY = 1e30; maxY = -1e30
    for i in range(vc):
        px, py, pz = r.v3()
        if py < minY: minY = py
        if py > maxY: maxY = py
        r.skip(12 + 8 + 16*extraUv)
        dt = r.u8()
        if dt == 0: idx(r, bI)
        elif dt in (1, 3):
            idx(r, bI); idx(r, bI); r.skip(4)
            if dt == 3: r.skip(36)
        elif dt in (2, 4):
            for _ in range(4): idx(r, bI)
            r.skip(16)
        else: raise Exception('deform %d @v%d' % (dt, i))
        r.skip(4)
    fc = r.i32(); r.skip(fc * vI)
    tc = r.i32()
    for _ in range(tc): text()
    mc = r.i32()
    for _ in range(mc):
        text(); text(); r.skip(16+12+4+12); r.u8(); r.skip(16+4)
        idx(r, tI); idx(r, tI); r.u8()
        shared = r.u8()
        if shared == 0: idx(r, tI)
        else: r.u8()
        text(); r.i32()
    bc = r.i32()
    bones = []
    for i in range(bc):
        nj = text(); ne = text(); pos = r.v3(); parent = idx(r, bI); r.i32()
        flags = r.u16()
        if flags & 0x0001: idx(r, bI)
        else: r.skip(12)
        if flags & (0x0100 | 0x0200): idx(r, bI); r.skip(4)
        if flags & 0x0400: r.skip(12)
        if flags & 0x0800: r.skip(24)
        if flags & 0x2000: r.i32()
        if flags & 0x0020:
            idx(r, bI); r.i32(); r.skip(4)
            ln = r.i32()
            for _ in range(ln):
                idx(r, bI)
                if r.u8() == 1: r.skip(24)
        bones.append((nj, ne, pos, parent))
    # morphs
    oc = r.i32()
    for _ in range(oc):
        text(); text(); r.u8(); mt = r.u8(); n2 = r.i32()
        for _ in range(n2):
            if mt in (0, 9): idx(r, mfI); r.skip(4)
            elif mt == 1: idx(r, vI, False); r.skip(12)
            elif mt == 2: idx(r, bI); r.skip(12+16)
            elif mt in (3,4,5,6,7): idx(r, vI, False); r.skip(16)
            elif mt == 8: idx(r, mI); r.u8(); r.skip(4*(4+3+1+3+4+1+4+4+4))
            elif mt == 10: idx(r, rI); r.u8(); r.skip(24)
            else: raise Exception('morph type %d' % mt)
    # display frames
    dc = r.i32()
    for _ in range(dc):
        text(); text(); r.u8(); n2 = r.i32()
        for _ in range(n2):
            if r.u8() == 0: idx(r, bI)
            else: idx(r, mfI)
    # rigid bodies
    rc = r.i32()
    rbs = []
    for _ in range(rc):
        nj = text(); ne = text(); bone = idx(r, bI)
        grp = r.u8(); mask = r.u16(); shape = r.u8()
        size = r.v3(); pos = r.v3(); rot = r.v3()
        mass = r.f(); ld = r.f(); ad = r.f(); r.skip(8)
        mode = r.u8()
        rbs.append(dict(nj=nj, ne=ne, bone=bone, grp=grp, mask=mask, shape=shape,
                        size=size, mass=mass, mode=mode))
    return dict(nameJp=nameJp, bones=bones, rbs=rbs, minY=minY, maxY=maxY)

if __name__ == '__main__':
    path = sys.argv[1]
    m = parse(path)
    bones = m['bones']; rbs = m['rbs']
    print('model:', m['nameJp'], ' bones:', len(bones), ' rbs:', len(rbs))
    print('vertex height: %.4f (minY=%.4f maxY=%.4f)' % (m['maxY']-m['minY'], m['minY'], m['maxY']))
    bminY = min(b[2][1] for b in bones); bmaxY = max(b[2][1] for b in bones)
    print('bone height:   %.4f (minY=%.4f maxY=%.4f)' % (bmaxY-bminY, bminY, bmaxY))
    dyn = {}
    for rb in rbs:
        if rb['mode'] != 0 and rb['bone'] >= 0 and rb['bone'] not in dyn:
            dyn[rb['bone']] = rb
    phys = set(dyn.keys())
    # chains
    roots = [b for b in phys if bones[b][3] not in phys]
    chains = []
    for root in sorted(roots):
        chain = [root]; cur = root
        while True:
            kids = [b for b in sorted(phys) if bones[b][3] == cur]
            if not kids: break
            chain.append(kids[0]); cur = kids[0]
        chains.append(chain)
    print('%d dynamic chains:' % len(chains))
    for ch in chains:
        names = [dyn[b]['nj'] for b in ch]
        print('  root_rb=%-24s len=%d bones=%s rb_names=%s' % (names[0], len(ch),
              [bones[b][0] for b in ch][:3], names))
    # head bone
    for i, b in enumerate(bones):
        if b[0] in (u'頭', u'首', u'下半身', u'センター'):
            print('bone[%d] %s pos=%s' % (i, b[0], b[2]))
