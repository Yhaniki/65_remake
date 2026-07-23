# -*- coding: utf-8 -*-
"""
Replace the 道具店/禮包店 tables in a TARGET iteminfo.dat with the SOURCE (CN clean) client's
rows, converting the names Simplified→Traditional (OpenCC s2twp) and re-encoding to GBK.

WHY: the SEA/international clean-DATA iteminfo.dat carries ~1,447 promotional/English gift packs
and ~470 prop model-ids that flood 禮包店/道具店. The repo's CN client (assets/閉撰敃氪/iteminfo.dat)
has the clean, curated set (28 gift packs / 188 props / 21 potions / 36 effects) with proper
Chinese names. This tool clears the four non-clothing shop categories in the target and refills
them from the CN source, in Traditional Chinese.

Categories replaced (道具店 + 禮包店 only — clothing/pets/etc. in the target are untouched):
    21000 道具   22000 藥水   24000 人物特效   14000 禮包

Format (both files): 12-byte header (int32 headA==2, headB, count) + count × 156-byte records,
name = char[44] @0x14 (GBK), self-inverse byte cipher dec = (0x1F9 - b) & 0xFF. A trailing pad
byte after the last record is preserved.

Usage:
    python tools/replace_shop_items_cn.py --target <clean\\DATA\\iteminfo.dat>
                                          --source <assets\\閉撰敃氪\\iteminfo.dat>
                                          [--config s2twp] [--no-traditional] [--dry-run]
Writes a timestamped .bak beside the target before overwriting.
"""
import os, sys, struct, argparse, io, shutil, datetime

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

PROP_CATS = {14000, 21000, 22000, 24000}
CAT_NAME = {14000: "禮包", 21000: "道具", 22000: "藥水", 24000: "特效"}
HEADER, STRIDE, NAME_OFF, NAME_MAX = 12, 156, 0x14, 44


def crypt(b):
    return (0x1F9 - b) & 0xFF


def load(path):
    d = open(path, "rb").read()
    headA = struct.unpack_from("<i", d, 0)[0]
    if headA != 2:
        raise SystemExit("[replace] %s headA=%d (expected 2 = CN 156-byte)" % (path, headA))
    recs, pos = [], HEADER
    while pos + STRIDE <= len(d):
        recs.append(bytearray(crypt(x) for x in d[pos:pos + STRIDE]))
        pos += STRIDE
    trailing = d[HEADER + len(recs) * STRIDE:]
    return bytearray(d[:HEADER]), recs, trailing


def cat_of(r):
    return struct.unpack_from("<i", r, 8)[0]


def id_of(r):
    return struct.unpack_from("<i", r, 0)[0]


def name_of(r):
    nb = bytes(r[NAME_OFF:NAME_OFF + NAME_MAX]).split(b"\x00")[0]
    try:
        return nb.decode("gbk")
    except Exception:
        return nb.decode("latin1")


def set_name(r, s):
    b = s.encode("gbk")[:NAME_MAX]          # traditional → GBK (verified to round-trip)
    while b:                                 # never leave a split multibyte char at the boundary
        try:
            b.decode("gbk")
            break
        except Exception:
            b = b[:-1]
    r[NAME_OFF:NAME_OFF + NAME_MAX] = b + b"\x00" * (NAME_MAX - len(b))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--target", required=True)
    ap.add_argument("--source", required=True)
    ap.add_argument("--config", default="s2twp")
    ap.add_argument("--no-traditional", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    cc = None
    if not args.no_traditional:
        import opencc
        cc = opencc.OpenCC(args.config)

    thdr, trecs, ttrail = load(args.target)
    _, srecs, _ = load(args.source)

    kept = [r for r in trecs if cat_of(r) not in PROP_CATS]
    removed = len(trecs) - len(kept)

    ins, per = [], {}
    for r in srecs:
        c = cat_of(r)
        if c in PROP_CATS:
            if cc is not None:
                nm = name_of(r)
                tw = cc.convert(nm) if nm else nm
                if tw and tw != nm:
                    set_name(r, tw)
            ins.append(r)
            per[c] = per.get(c, 0) + 1

    out = kept + ins

    # id-collision sanity check (buy path keys on item id) — report, don't block
    kept_ids = {}
    for r in kept:
        kept_ids.setdefault(id_of(r), 0)
        kept_ids[id_of(r)] += 1
    coll = sum(1 for r in ins if id_of(r) in kept_ids)

    print("[replace] target %d recs → keep %d (removed %d prop/giftpack)" % (len(trecs), len(kept), removed))
    print("[replace] source inserted %d rows:" % len(ins),
          "  ".join("%s(%d)=%d" % (CAT_NAME[c], c, per.get(c, 0)) for c in (21000, 22000, 24000, 14000)))
    print("[replace] new total = %d records;  id-collisions with kept rows: %d" % (len(out), coll))
    # sample of converted names
    samp = [name_of(r) for r in ins[:8]]
    print("[replace] sample names:", "  ".join(samp))

    if args.dry_run:
        print("[replace] --dry-run: not writing")
        return

    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    bak = args.target + ".bak-" + stamp
    shutil.copy2(args.target, bak)
    print("[replace] backup -> %s" % bak)

    struct.pack_into("<i", thdr, 8, len(out))   # patch header count (keep headA/headB)
    with open(args.target, "wb") as f:
        f.write(thdr)
        for r in out:
            f.write(bytes(crypt(x) for x in r))
        f.write(ttrail)
    print("[replace] wrote %s (%d records)" % (args.target, len(out)))


if __name__ == "__main__":
    main()
