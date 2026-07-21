# -*- coding: utf-8 -*-
"""
離線破解 NXPatch 的 .nx 譜面容器 —— 不用進遊戲、不用連伺服器。

原理（逆向自 NXPatch.exe，細節見 NX_FORMAT.md §2）：
  patcher 用 inline hook 把遊戲的解密函式 0xb1c950 攔到自己的 .g22 段(0x124e000)，
  在原本的 0x3D09 LCG **之前**多加一層固定變換：

      b ^= 0xA7 ;  b -= 0x29 ;  b = (b*0xF9)&0xFF ;  b = ror8(b,3)

  這層**沒有金鑰、與位置無關**（純混淆）。還原它之後就是原本的 0x3D09 LCG。

  而 LCG 的 seed 也不必連線拿 —— blob 解出來的前 300 bytes 是「重複表頭」，內容就是
  檔案 0x1C8 那份**明文**表頭。拿它當已知明文，暴力 2^24 個等價 state 即可還原
  (keystream 只用 state 的 bit16-23 → 只依賴低 24 位)。seed 會在多個檔之間重複用，
  所以先試已知 pool，沒中才暴力。

用法：
    python crack_nx.py "H:/sdo/Super Dance Online/patch music/*.nx"            # 只驗證/列出 seed
    python crack_nx.py "…/patch music/*.nx" -o out/                            # 順便輸出明文譜
    python decode_chart.py out/sdom2818K.nx.plain --diff hard                   # 再解析內容

輸出的 .plain = 明文 StepFile（300B 表頭 + note 資料），可直接餵給 decode_chart.py。
"""
from __future__ import annotations
import argparse, glob, os, struct, sys
import numpy as np

M = 0x3D09
HDR_OFF, BLOB_OFF = 0x1C8, 0x2F4   # 明文 StepFile 表頭 / 加密 blob
KNOWN = 300                        # 重複表頭長度（拿來當已知明文）


def _utf8_stdout():
    out = getattr(sys, "stdout", None)
    if out is not None and hasattr(out, "reconfigure"):
        try: out.reconfigure(encoding="utf-8", errors="replace")
        except Exception: pass


def undo_outer(b: bytes) -> np.ndarray:
    """還原 patcher 外層固定變換（無金鑰）。"""
    v = np.frombuffer(b, dtype=np.uint8).astype(np.uint16)
    v = (v ^ 0xA7) & 0xFF
    v = (v - 0x29) & 0xFF
    v = ((v * 0xF9) & 0xFF).astype(np.uint8)
    return ((v >> 3) | (v << 5)).astype(np.uint8)


def keystream(state1: int, n: int) -> np.ndarray:
    """0x3D09 LCG keystream；state1 = seed*M 之後的狀態（只需低 24 位）。"""
    ks = np.empty(n, dtype=np.uint8); st = state1 & 0xFFFFFF
    for i in range(n):
        ks[i] = (st >> 16) & 0xFF
        st = (st * M) & 0xFFFFFF
    return ks


def _dec(st1: np.ndarray, state1: int, n: int) -> bytes:
    ks = keystream(state1, n)
    return bytes(((st1[:n].astype(np.int16) - ks.astype(np.int16)) & 0xFF).astype(np.uint8))


def looks_valid(st1: np.ndarray, state1: int, hdr: bytes, bloblen: int) -> bool:
    """結構驗證：解出的重複表頭要是合法 StepFile。

    刻意 **不** 要求跟容器表頭逐位元組相同 —— 少數檔的 metadata 會差幾個 byte，
    且容器表頭的 address_end(+296) 是垃圾值。
    也 **不** 能要求 address_end == blob 長度 —— 有內嵌 dps/png/ogg 的檔，note 資料
    只佔 blob 前段（例：sdom2818 addr_end=620260，blob 卻有 1384076）。
    改用「四個 address 單調遞增且落在 blob 內」這個結構條件。
    """
    d = _dec(st1, state1, KNOWN)
    if d[4:8] != b"gn\0\0":
        return False
    if struct.unpack_from("<I", d, 0)[0] != struct.unpack_from("<I", hdr, 0)[0]:
        return False                                   # fileId 要跟容器一致
    a0, a1, a2, a3 = struct.unpack_from("<4I", d, 284)
    return a0 == 300 and 300 <= a1 <= a2 <= a3 <= bloblen


def brute_state1(st1: np.ndarray, hdr: bytes, n: int = 16) -> int | None:
    """用前 n bytes 已知明文，向量化掃 2^24 個候選 state。"""
    tgt = np.frombuffer(bytes((int(st1[i]) - hdr[i]) & 0xFF for i in range(n)), dtype=np.uint8)
    s = np.arange(1 << 24, dtype=np.uint32); mask = np.uint32((1 << 24) - 1)
    keep = np.ones(len(s), dtype=bool); cur = s.copy()
    for i in range(n):
        kb = ((cur >> np.uint32(16)) & np.uint32(0xFF)).astype(np.uint8)
        keep &= (kb == tgt[i])
        cur = (cur * np.uint32(M)) & mask
    c = s[keep]
    return int(c[0]) if len(c) else None


def _stage1(raw: bytes, n: int, outer: bool) -> np.ndarray:
    """取 blob 前 n bytes；outer=True 先還原 patcher 外層變換。"""
    b = raw[BLOB_OFF:BLOB_OFF + n] if n else raw[BLOB_OFF:]
    return undo_outer(b) if outer else np.frombuffer(b, dtype=np.uint8)


def crack(path: str, pool: list[int]):
    """回傳 (state1, raw, outer, how)；失敗時 state1 為 None。

    自動判斷有沒有 patcher 外層變換：
      patch music\\*.nx → 有外層；music\\*.gn（已安裝/離線單機）→ 沒有。
    """
    raw = open(path, "rb").read()
    if len(raw) < BLOB_OFF + KNOWN or raw[HDR_OFF + 4:HDR_OFF + 6] != b"gn":
        return None, None, None, "not a container"
    hdr = raw[HDR_OFF:HDR_OFF + KNOWN]
    bloblen = len(raw) - BLOB_OFF
    for outer in (True, False):                       # 先試 .nx（有外層），再試純 .gn
        st1 = _stage1(raw, KNOWN, outer)
        for s in pool:                                # seed 會跨檔重複用 → 先試已知的（免暴力）
            if looks_valid(st1, s, hdr, bloblen):
                return s, raw, outer, "pool"
        s = brute_state1(st1, hdr)
        if s is not None and looks_valid(st1, s, hdr, bloblen):
            pool.insert(0, s)
            return s, raw, outer, "brute"
    return None, None, None, "no candidate"


def decrypt(raw: bytes, state1: int, outer: bool) -> bytes:
    """完整解密 blob → 明文 StepFile。"""
    st1 = _stage1(raw, 0, outer)
    ks = keystream(state1, len(st1))
    return ((st1.astype(np.int16) - ks.astype(np.int16)) & 0xFF).astype(np.uint8).tobytes()


def main() -> int:
    _utf8_stdout()
    ap = argparse.ArgumentParser(description="離線破解 .nx 譜面（外層固定變換 + 0x3D09 LCG）")
    ap.add_argument("pattern", help='檔案 glob，例如 "…/patch music/*.nx"')
    ap.add_argument("-o", "--out", help="輸出明文譜的資料夾（省略則只驗證/列 seed）")
    ap.add_argument("-v", "--verbose", action="store_true", help="每個檔都印")
    a = ap.parse_args()

    files = sorted(glob.glob(a.pattern))
    if not files:
        print(f"沒有符合的檔案：{a.pattern}", file=sys.stderr); return 1

    pool: list[int] = []
    ok, failed = 0, []
    for i, f in enumerate(files, 1):
        s, raw, outer, how = crack(f, pool)
        name = os.path.basename(f)
        if s is None:
            failed.append((name, how))
            print(f"  [{i}/{len(files)}] {name:22} 失敗：{how}")
            continue
        ok += 1
        if a.out:
            os.makedirs(a.out, exist_ok=True)
            open(os.path.join(a.out, name + ".plain"), "wb").write(decrypt(raw, s, outer))
        if a.verbose or how == "brute":
            layer = "外層+LCG" if outer else "純LCG"
            print(f"  [{i}/{len(files)}] {name:22} state1={s:#08x} ({how}, {layer})")

    print(f"\n{ok}/{len(files)} 成功；distinct seed = {len(pool)}")
    if a.out:
        print(f"明文譜已輸出到 {a.out}（用 decode_chart.py 解析）")
    if failed:
        print("失敗清單：", failed[:20])
    return 0 if not failed else 2


if __name__ == "__main__":
    raise SystemExit(main())
