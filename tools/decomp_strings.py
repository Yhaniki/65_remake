"""Resolve Ghidra DAT_ addresses in sdo.bin.c to the string literals they point at.

Usage:
  python tools/decomp_strings.py strings <substr>        # find strings + VA
  python tools/decomp_strings.py xref <VA-hex>           # decompiled lines mentioning that VA
  python tools/decomp_strings.py func <FUN_xxxxxxxx>     # print function, DAT_ refs annotated
"""
import re
import struct
import sys

BIN = r"H:\65_remake\assets\閉撰敃氪\sdo.bin"
SRC = r"H:\sdo_cn\sdo.bin.c"


def sections(b):
    lfa = struct.unpack_from("<I", b, 0x3C)[0]
    nsec = struct.unpack_from("<H", b, lfa + 6)[0]
    optsz = struct.unpack_from("<H", b, lfa + 20)[0]
    base = struct.unpack_from("<I", b, lfa + 24 + 28)[0]
    out = []
    off = lfa + 24 + optsz
    for _ in range(nsec):
        va, = struct.unpack_from("<I", b, off + 12)
        rsz, rptr = struct.unpack_from("<II", b, off + 16)
        out.append((base + va, rptr, rsz))
        off += 40
    return out


def load():
    b = open(BIN, "rb").read()
    secs = sections(b)
    return b, secs


def va2off(secs, va):
    for sva, rptr, rsz in secs:
        if sva <= va < sva + rsz:
            return rptr + (va - sva)
    return None


def read_str(b, secs, va, maxlen=200):
    o = va2off(secs, va)
    if o is None:
        return None
    end = b.find(b"\0", o, o + maxlen)
    if end < 0:
        return None
    raw = b[o:end]
    if not raw:
        return ""
    try:
        return raw.decode("gbk")
    except UnicodeDecodeError:
        return raw.decode("latin1")


def printable(s):
    return s is not None and len(s) >= 2 and all(ord(c) >= 32 or c == "\t" for c in s)


def cmd_strings(sub):
    b, secs = load()
    pat = sub.encode("gbk") if any(ord(c) > 127 for c in sub) else sub.encode()
    for m in re.finditer(re.escape(pat), b):
        o = m.start()
        # walk back to start of C string
        st = o
        while st > 0 and b[st - 1] != 0 and o - st < 120:
            st -= 1
        for sva, rptr, rsz in secs:
            if rptr <= st < rptr + rsz:
                va = sva + (st - rptr)
                s = read_str(b, secs, va)
                if printable(s):
                    print(f"{va:08x}  {s}")
                break


def cmd_xref(va_hex):
    va = int(va_hex, 16)
    key = f"{va:08x}"
    src = open(SRC, encoding="utf-8", errors="replace").read().split("\n")
    for i, l in enumerate(src):
        if key in l:
            print(f"{i + 1}: {l.strip()[:200]}")


FUNC_RE = re.compile(r"^\w[\w \*]*\b(FUN_[0-9a-f]{8}|__\w+)\s*\(")


def cmd_func(name, ctx=0):
    b, secs = load()
    src = open(SRC, encoding="utf-8", errors="replace").read().split("\n")
    starts = [i for i, l in enumerate(src) if re.match(rf"^\w.*\b{re.escape(name)}\s*\(", l) and (i + 1 < len(src)) and src[i + 1].startswith("{")]
    if not starts:
        print(f"!! {name} definition not found")
        return
    for st in starts:
        # back up over return type line
        head = st
        while head > 0 and src[head - 1].strip() and not src[head - 1].startswith("}") and not src[head - 1].startswith("/*"):
            head -= 1
        i = st + 1
        depth = 0
        out = []
        while i < len(src):
            out.append(src[i])
            depth += src[i].count("{") - src[i].count("}")
            if depth <= 0 and src[i].startswith("}"):
                break
            i += 1
        body = "\n".join(src[head:st + 1] + out)
        print(annotate(body, b, secs))


def annotate(body, b, secs):
    def rep(m):
        va = int(m.group(1), 16)
        s = read_str(b, secs, va)
        if printable(s):
            return f'{m.group(0)}/*"{s}"*/'
        return m.group(0)

    return re.sub(r"(?:DAT|PTR|UNK)_00([0-9a-f]{6})", lambda m: rep_full(m, b, secs), body)


def rep_full(m, b, secs):
    va = int("00" + m.group(1), 16)
    s = read_str(b, secs, va)
    if printable(s):
        return f'{m.group(0)}/*"{s}"*/'
    return m.group(0)


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "strings":
        cmd_strings(sys.argv[2])
    elif cmd == "xref":
        cmd_xref(sys.argv[2])
    elif cmd == "func":
        cmd_func(sys.argv[2])
    else:
        print(__doc__)
