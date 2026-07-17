# -*- coding: utf-8 -*-
"""
Bake the Taiwan「櫻式搖滾」client's iteminfo.dat into shop_names_tw.tsv — a UTF-8
"category<TAB>modelId<TAB>name" sidecar the remake overlays at runtime
(Sdo.Game.AvatarItemCatalog.ApplyTwNames / SynthName, parsed by Sdo.Shop.ShopNameTwSidecar).

WHY A SEPARATE TOOL (not the runtime IteminfoReader): the TW build is a DIFFERENT on-disk
format from the CN client the remake normally reads —
    CN : header int32 headA == 2, 156-byte records, GBK/CP936 Simplified names
    TW : header int32 headA == 1, 152-byte records, Big5 Traditional names
Both share the front field layout (int32 id@0x00, modelId@0x04, category@0x08, name@0x14)
and the self-inverse byte cipher dec = (0x1F9 - b) & 0xFF. This script auto-detects the
format from headA, decodes the names once (Windows/py both have Big5), and keys the output
by (category, modelId) — NOT the item id — because ids are renumbered between clients and the
rows we most want to name are synth mesh-only rows that have no iteminfo id.

Usage:
    python tools/build_shop_names_tw.py [--in <iteminfo.dat>] [--out <tsv> ...] [--dry-run]
Defaults: --in  H:\\sdo_tw\\3熱舞 Online(櫻式搖滾)\\iteminfo.dat
          --out <data-root>/shop_names_tw.tsv  AND  <repo>/tools/data/shop_names_tw.tsv
          (data-root read from <repo>/data_root.txt; editor + clean-DATA runtime read it there,
           and tools/data/ is the committed copy package_build.ps1 ships into <exe>/DATA.)
"""
import os, sys, struct, argparse, io

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_IN = r"H:\sdo_tw\3熱舞 Online(櫻式搖滾)\iteminfo.dat"

# categories that map to an avatar mesh token (slot+gender) — the only rows the shop can use.
# male 1..9 / 50 , female 101..109 / 150 . (200/201/203 outfit-sets are handled via setinfo, not here.)
MESH_CATS = {1,2,3,4,5,6,7,8,9,50, 101,102,103,104,105,106,107,108,109,150}
SLOT = {1:"髮型",2:"上衣",3:"下裝",4:"手套",5:"鞋子",6:"表情",7:"眼鏡",8:"翅膀",9:"项链",50:"連身",
        101:"髮型",102:"上衣",103:"下裝",104:"手套",105:"鞋子",106:"表情",107:"眼鏡",108:"翅膀",109:"项链",150:"連身"}


def crypt(b): return (0x1F9 - b) & 0xFF


def decode_name(nb, encs):
    z = nb.find(0)
    if z >= 0:
        nb = nb[:z]
    nb = nb.rstrip(b"\x00 ")
    if not nb:
        return ""
    for e in encs:
        try:
            s = nb.decode(e)          # strict: a bad decode raises → we skip that row rather than emit mojibake
            if any(ord(c) < 0x20 for c in s):
                return None           # control chars → not a real name (corrupt / non-name field)
            return s
        except Exception:
            pass
    return None


def parse(path):
    with open(path, "rb") as f:
        data = f.read()
    headA, headB, cnt = struct.unpack_from("<iii", data, 0)
    if headA == 2:
        stride, encs, tag = 156, ("gbk",), "CN(簡體)"
    elif headA == 1:
        stride, encs, tag = 152, ("big5", "cp950", "gbk"), "TW(繁體)"
    else:
        raise SystemExit("[tw] unexpected headA=%d (expected 1=TW or 2=CN)" % headA)
    print("[tw] %s  size=%d headA=%d headB=%d headerCount=%d stride=%d" % (tag, len(data), headA, headB, cnt, stride))
    rows, encs_used = [], encs
    pos = 12
    while pos + stride <= len(data):
        rec = bytes(crypt(x) for x in data[pos:pos + stride])
        iid, modelId, cat = struct.unpack_from("<iii", rec, 0)
        name = decode_name(rec[0x14:0x14 + 44], encs)
        rows.append((iid, modelId, cat, name))
        pos += stride
    return rows, encs_used


def scrypt(b): return (0xF9 - b) & 0xFF   # setinfo self-inverse cipher (mirrors Sdo.Shop/SetinfoReader.cs)


def parse_setinfo(path, encs):
    """setinfo.dat → {setId: [component modelIds]}. RAW int32 count, then count × 364-byte encrypted records;
    each record = int32 setId + 6 × (int32 modelId, int16 flag, int16 pad, char[52] name)."""
    with open(path, "rb") as f:
        data = f.read()
    count = struct.unpack_from("<i", data, 0)[0]
    sets = {}
    for k in range(count):
        pos = 4 + k * 364
        if pos + 364 > len(data):
            break
        rec = bytes(scrypt(x) for x in data[pos:pos + 364])
        setId = struct.unpack_from("<i", rec, 0)[0]
        comps = []
        for c in range(6):
            b = 4 + c * 60
            mid = struct.unpack_from("<i", rec, b)[0]
            if mid in (0x7FFFFFFF, 0):
                continue
            comps.append(mid)
        if comps:
            sets[setId] = comps
    return sets


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", default=DEFAULT_IN)
    ap.add_argument("--out", dest="out", action="append", default=None)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    if not os.path.isfile(args.inp):
        raise SystemExit("[tw] input not found: %s" % args.inp)

    # default outputs: data root (from data_root.txt) + committed tools/data copy
    outs = args.out
    if not outs:
        outs = []
        droot = None
        drfile = os.path.join(REPO, "data_root.txt")
        if os.path.isfile(drfile):
            with open(drfile, encoding="utf-8") as f:
                droot = f.readline().strip().strip('"')
        if droot and os.path.isdir(droot):
            outs.append(os.path.join(droot, "shop_names_tw.tsv"))
        else:
            print("[tw] WARNING: data_root.txt missing/invalid (%r) — skipping data-root output" % droot)
        outs.append(os.path.join(REPO, "tools", "data", "shop_names_tw.tsv"))

    rows, encs = parse(args.inp)

    # keep clothing rows with a real name + a real shop modelId; dedup by (cat, modelId), first wins.
    def is_shop(m): return 0 < m < 900000
    seen, kept, per = {}, [], {}
    named_total = skipped_nomodel = skipped_dup = 0
    for iid, modelId, cat, name in rows:
        if cat not in MESH_CATS or not name:
            continue
        named_total += 1
        if not is_shop(modelId):
            skipped_nomodel += 1
            continue
        key = (cat, modelId)
        if key in seen:
            skipped_dup += 1
            continue
        seen[key] = name
        kept.append((cat, modelId, name))
        per[cat] = per.get(cat, 0) + 1

    kept.sort(key=lambda r: (r[0], r[1]))
    text = "".join("%d\t%d\t%s\n" % (c, m, n) for c, m, n in kept)

    print("[tw] clothing rows named=%d  kept=%d  (skipped: no-shop-model=%d dup=%d)"
          % (named_total, len(kept), skipped_nomodel, skipped_dup))
    for cat in sorted(per):
        print("       %-4d %-6s %-5s : %d" % (cat, SLOT[cat], "MAN" if cat < 100 else "WOMAN", per[cat]))
    print("[tw] sample:", "  ".join("%d=%s" % (m, n) for c, m, n in kept[:6]))

    # ---- outfit sets: join TW iteminfo Outfit rows (cat 200/201/203, name+gender) with TW setinfo (components) ----
    # setinfo.dat sits beside the iteminfo.dat we were given.
    set_lines, set_n = [], 0
    setinfo_path = os.path.join(os.path.dirname(args.inp), "setinfo.dat")
    if os.path.isfile(setinfo_path):
        outname = {}   # setId -> (name, isMale)  from the iteminfo Outfit row (modelId == setId)
        for iid, modelId, cat, name in rows:
            if cat in (200, 201, 203) and name:
                if cat == 201:
                    male = True
                elif cat == 200:
                    male = False
                else:  # 203 mixed → gender from the name (男装/女装)
                    male = ("男" in name) or ("女" not in name)
                outname.setdefault(modelId, (name, male))
        sets = parse_setinfo(setinfo_path, encs)
        defs = []
        for setId, comps in sets.items():
            if setId not in outname:
                continue                        # a set with no Outfit-row name (gift bundle etc.) — no display name
            name, male = outname[setId]
            comps = [m for m in comps if is_shop(m)]
            if not comps:
                continue
            defs.append((setId, male, comps, name))
        defs.sort(key=lambda d: d[0])
        set_n = len(defs)
        for setId, male, comps, name in defs:
            set_lines.append("%d\t%s\t%s\t%s\n" % (setId, "M" if male else "F", ",".join(str(m) for m in comps), name))
        print("[tw] outfit sets: setinfo=%d  named+componented=%d" % (len(sets), set_n))
        for setId, male, comps, name in defs[:8]:
            print("       %d %-5s 「%s」 comps=%s" % (setId, "MAN" if male else "WOMAN", name, comps))
    else:
        print("[tw] WARNING: setinfo.dat not found beside input (%s) — skipping sets" % setinfo_path)
    settext = "".join(set_lines)

    if args.dry_run:
        print("[tw] --dry-run: not writing")
        return
    for o in outs:
        os.makedirs(os.path.dirname(o), exist_ok=True)
        with open(o, "w", encoding="utf-8", newline="") as f:   # UTF-8, no BOM, LF
            f.write(text)
        print("[tw] wrote %d names -> %s" % (len(kept), o))
        so = os.path.join(os.path.dirname(o), "shop_sets_tw.tsv")
        with open(so, "w", encoding="utf-8", newline="") as f:
            f.write(settext)
        print("[tw] wrote %d sets  -> %s" % (set_n, so))


if __name__ == "__main__":
    main()
