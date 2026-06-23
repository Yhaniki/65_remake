#!/usr/bin/env python3
# Audit every scene's mapobj assets: per-submesh material names, whether the referenced
# texture resolves on disk, whether it carries non-trivial alpha (DXT3/DXT5), and the mesh
# bounding box. Mirrors MshLoader.cs parsing. Run from repo root.
import os, struct, sys, glob

ROOT = os.path.join("assets", "sdox_offline", "Extracted")
MAPOBJ = os.path.join(ROOT, "SCENE", "MAPOBJ")

FVF_STRIDES = {0x1158, 0x115A, 0x115C, 0x112, 0x142, 0x152}

def u32(d, o): return struct.unpack_from("<I", d, o)[0]
def f32(d, o): return struct.unpack_from("<f", d, o)[0]

def read_cstr(d, o, mx):
    n = 0
    while n < mx and o + n < len(d) and d[o + n] != 0:
        n += 1
    return d[o:o+n].decode("ascii", "replace")

def scan_next_submesh(d, start):
    off = start
    while off + 12 <= len(d):
        fvf = u32(d, off)
        if fvf in FVF_STRIDES:
            idx = u32(d, off + 4); opt = u32(d, off + 8)
            if 0 < idx < 10000000 and (idx & 1) == 0 and opt == 101:
                vp = off + 12 + idx
                if vp + 8 <= len(d):
                    vsz = u32(d, vp); st = u32(d, vp + 4)
                    if 16 <= st <= 64 and vsz > 0 and vsz % st == 0:
                        return off
        off += 4
    return len(d)

def parse_msh(path):
    d = open(path, "rb").read()
    if len(d) < 16 or d[0:12] != b"Mesh00000030":
        return None
    p = 12
    submesh_count = u32(d, p); p += 4
    if submesh_count <= 0 or submesh_count > 16:
        return {"err": f"bad submeshCount={submesh_count}"}
    subs = []
    for s in range(submesh_count):
        if p + 12 > len(d): break
        fvf = u32(d, p); p += 4
        idx_size = u32(d, p); p += 4
        opt = u32(d, p); p += 4
        if idx_size <= 0 or (idx_size & 1) != 0 or p + idx_size > len(d):
            subs.append({"err": "bad idx", "fvf": hex(fvf)}); break
        p += idx_size
        if p + 8 > len(d): break
        vert_size = u32(d, p); p += 4
        stride = u32(d, p); p += 4
        if stride <= 0 or vert_size <= 0 or vert_size % stride != 0:
            subs.append({"err": "bad vert", "fvf": hex(fvf)}); break
        vcount = vert_size // stride
        vert_off = p; p += vert_size
        # bounds from xyz at vertex start
        mn = [1e30, 1e30, 1e30]; mx = [-1e30, -1e30, -1e30]
        for i in range(vcount):
            b = vert_off + i * stride
            for k in range(3):
                v = f32(d, b + k*4)
                if v < mn[k]: mn[k] = v
                if v > mx[k]: mx[k] = v
        p += 24  # 6 reserved u32
        if p + 4 > len(d): break
        num_mat = u32(d, p); p += 4
        first_mat = p
        names = []
        m = 0
        while m < num_mat and p + 408 <= len(d):
            names.append(read_cstr(d, p + 17*4, 320))
            p = first_mat + (m + 1) * 408
            m += 1
        size = [mx[k]-mn[k] for k in range(3)]
        ctr = [(mx[k]+mn[k])/2 for k in range(3)]
        subs.append({"fvf": hex(fvf), "stride": stride, "vcount": vcount,
                     "mats": names, "size": size, "center": ctr})
        if s < submesh_count - 1:
            nx = scan_next_submesh(d, p)
            if nx >= len(d): break
            p = nx
    return {"submeshes": subs}

def dds_info(path):
    """Return (fourcc, has_alpha, min_alpha) for a DDS, sampling alpha for DXT3/DXT5."""
    d = open(path, "rb").read()
    if len(d) < 128 or d[0:4] != b"DDS ":
        return ("notdds", None, None)
    fourcc = d[84:88].decode("ascii", "replace")
    h = struct.unpack_from("<I", d, 12)[0]; w = struct.unpack_from("<I", d, 16)[0]
    if fourcc == "DXT1":
        return (fourcc, False, 255)
    if fourcc not in ("DXT3", "DXT5"):
        # uncompressed? check pixelformat flags/bitcount for alpha mask
        return (fourcc, None, None)
    bw = (w + 3)//4; bh = (h + 3)//4
    off = 128
    minA = 255
    bi = off
    for by in range(bh):
        for bx in range(bw):
            if bi + 16 > len(d):
                break
            if fourcc == "DXT3":
                # 8 bytes 4-bit alpha
                for k in range(8):
                    ab = d[bi + k]
                    lo = (ab & 0xF) * 255 // 15
                    hi = ((ab >> 4) & 0xF) * 255 // 15
                    if lo < minA: minA = lo
                    if hi < minA: minA = hi
            else:  # DXT5
                a0 = d[bi]; a1 = d[bi+1]
                if a0 < minA: minA = a0
                if a1 < minA: minA = a1
            bi += 16
    return (fourcc, minA < 250, minA)

def resolve_tex(folder, matname):
    """Mirror ResolveDds: try exact filename, then any .dds with matching stem."""
    if not matname:
        return None
    name = os.path.basename(matname.replace("\\", "/"))
    direct = os.path.join(folder, name)
    if os.path.isfile(direct):
        return direct
    stem = os.path.splitext(name)[0].lower()
    for fpath in glob.glob(os.path.join(folder, "*")):
        if fpath.lower().endswith(".dds") and os.path.splitext(os.path.basename(fpath))[0].lower() == stem:
            return fpath
    return None

def main():
    targets = sys.argv[1:]  # optional folder filters (substring)
    for dirpath, dirs, files in sorted(os.walk(MAPOBJ)):
        mshes = [f for f in files if f.lower().endswith(".msh")]
        if not mshes:
            continue
        rel = os.path.relpath(dirpath, MAPOBJ).replace("\\", "/")
        if targets and not any(t.lower() in rel.lower() for t in targets):
            continue
        for msh in sorted(mshes):
            res = parse_msh(os.path.join(dirpath, msh))
            label = f"{rel}/{msh}"
            if res is None:
                print(f"[{label}] NOT A MESH"); continue
            if "err" in res:
                print(f"[{label}] ERR {res['err']}"); continue
            for si, sm in enumerate(res["submeshes"]):
                if "err" in sm:
                    print(f"[{label}] sub{si} PARSE-ERR {sm['err']} fvf={sm.get('fvf')}")
                    continue
                sz = sm["size"]
                szs = f"({sz[0]:.0f},{sz[1]:.0f},{sz[2]:.0f})"
                ct = sm["center"]
                cts = f"({ct[0]:.0f},{ct[1]:.0f},{ct[2]:.0f})"
                matparts = []
                for mt in sm["mats"]:
                    tp = resolve_tex(dirpath, mt)
                    if tp is None:
                        matparts.append(f"{mt}=MISSING")
                    else:
                        fcc, has_a, minA = dds_info(tp)
                        tag = fcc
                        if has_a:
                            tag += f"+ALPHA(min{minA})"
                        elif fcc in ("DXT3","DXT5"):
                            tag += "(opaque)"
                        matparts.append(f"{os.path.basename(tp)}[{tag}]")
                print(f"[{label}] sub{si} fvf={sm['fvf']} str={sm['stride']} vc={sm['vcount']} sz={szs} ctr={cts} :: {', '.join(matparts) if matparts else '(no mats)'}")

if __name__ == "__main__":
    main()
