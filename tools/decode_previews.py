#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
decode_previews.py — 把官方「試聽」加密檔 exper/<fileId>.sdm 正確解成可播 .ogg。

問題：試聽 .sdm 是「孤兒編碼」(Page1 psize≈4169~4814，與主曲 2828 標準模板不符)，一般
keyless 解碼 (sdm_decoder) 會硬拼出結構壞掉的 ogg (ffmpeg「Invalid Setup header /
Codebook lookup」、FMOD「Unsupported audio format」)。

原理：Vorbis 的 setup header(codebook) 在 Page1，跨「DES 區(ogg[0:512],無 key 解不出)」與
tail(=transform_505(sdm[513:]))。可拿「已知正確、且 codebook 相同」的試聽 ogg 當 donor,
取其 Page0+Page1(id+comment+SETUP),接上本曲音訊頁:

    decoded = fix_ogg_serials( donor[0 : 58+donor_psize] + tail[p2_off:] )
    p2_off = tail.find(b"OggS")   # 本曲第一個音訊頁(Page2)

音訊來自本曲 sdm(正確歌曲),只有可重用的表頭向 donor 借。

**donor 必須 codebook 相同,不能只看 psize!** (psize 一樣 codebook 不一定一樣 → 用錯 donor
會「解得出但是雜音」,ffmpeg 不報錯。) 正確配對鍵 = codebook 指紋 = 本曲 setup 尾端 bytes
(tail[p2_off-SIG:p2_off]);同 codebook 的 donor 其 Page1 尾端會完全相同。挑到後再驗
common-suffix 夠長(整段 setup 都吻合,僅前 ~512B DES 區不同)才用。

donor 池 = `assets/閉撰敃氪/music/exper/*.ogg`(官方已解好的試聽,約 10 種 codebook)。試聽 sdm
來源依序:`新增資料夾/exper` → 各版本 Music/exper → SDO-X exper。成員曲目 = song_table.csv 的 k 譜面。

用法(需 PATH 有 ffprobe，或放 H:/bms/bak/tools/dist)：
  python tools/decode_previews.py
"""
import os
import sys
import glob
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import song_table                                              # noqa: E402
sys.path.insert(0, r"H:/bms/tools")
from bms_sdo.sdm_codec import transform_505, fix_ogg_serials  # noqa: E402

REPO = os.path.dirname(HERE)
M = os.path.join(REPO, "assets", "sdox_offline", "music")
EXPER = os.path.join(M, "exper")
DONOR_DIR = os.path.join(REPO, "assets", "閉撰敃氪", "music", "exper")

SDM_SRCS = [r"H:/sdo_tw/新增資料夾/Music/exper"]
SDM_SRCS += sorted(glob.glob(r"H:/sdo_tw/*/Music/exper"))
SDM_SRCS += [r"D:/program/SDO-X/SDO-X Alchemist World/music/exper"]
SDM_SRCS = [d for i, d in enumerate(SDM_SRCS) if d not in SDM_SRCS[:i]]


def _ffprobe():
    for c in ("ffprobe", r"H:/bms/bak/tools/dist/ffprobe.exe"):
        try:
            subprocess.run([c, "-version"], capture_output=True)
            return c
        except OSError:
            continue
    raise SystemExit("找不到 ffprobe（放進 PATH 或 H:/bms/bak/tools/dist/）")


FFPROBE = _ffprobe()


def ne(p):
    return p and os.path.isfile(p) and os.path.getsize(p) > 0


def psize(o):
    return 27 + o[58 + 26] + sum(o[58 + 27:58 + 27 + o[58 + 26]]) if len(o) > 62 and o[58:62] == b"OggS" else None


def valid(f):
    p = subprocess.run([FFPROBE, "-v", "error", "-show_entries", "stream=codec_name", "-of", "csv=p=0", f],
                       capture_output=True, text=True)
    return p.returncode == 0 and "vorbis" in (p.stdout or "").lower()


SIG = 96            # codebook fingerprint length (setup-tail bytes just before the first audio page)
MIN_SUFFIX = 1200   # required common-suffix (donor Page1 vs song setup) to accept a donor as same-codebook


def common_suffix(a, b):
    n = min(len(a), len(b)); i = 0
    while i < n and a[-1 - i] == b[-1 - i]:
        i += 1
    return i


# Extra donor sources for codebooks NOT present in the exper pool: the MAIN song oggs. A song's preview is
# a clip of its main song at the same encoder quality, so the main ogg's Page1 carries the same codebook
# (verified by a long common-suffix). Covers batches whose preview ogg was never shipped (e.g. sdom0259~0273).
MAIN_DONOR_DIRS = [M, os.path.join(REPO, "assets", "閉撰敃氪", "music")]


def _index_ogg(path, by_sig, header_only):
    try:
        with open(path, "rb") as fh:
            d = fh.read(8192 if header_only else -1)
    except OSError:
        return
    P = psize(d)
    if not P or d[:4] != b"OggS" or len(d) < 58 + P:
        return
    page1 = d[:58 + P]                           # Page0 + full Page1 (id + comment + SETUP/codebook)
    by_sig.setdefault(page1[58 + P - SIG:58 + P], []).append(page1)


def build_donor_index():
    """Two tiers of {codebook_sig: [Page0+Page1 ...]}: exper donors (preferred — a preview header, right
    Page0 blocksize) and main-song donors (fallback, only for codebooks the exper pool lacks)."""
    exper, main = {}, {}
    for o in sorted(glob.glob(os.path.join(DONOR_DIR, "*.ogg"))):
        _index_ogg(o, exper, header_only=False)
    for d in MAIN_DONOR_DIRS:
        for o in glob.glob(os.path.join(d, "sdom*.ogg")):
            _index_ogg(o, main, header_only=True)
    return exper, main


def _best_donor(setup_tail, by_sig):
    best = None
    for head in by_sig.get(setup_tail[-SIG:], []):
        cs = common_suffix(head, setup_tail)     # confirm the whole setup matches, not just the SIG bytes
        if cs > (best[1] if best else -1):
            best = (head, cs)
    return best


def decode(sdm_bytes, exper_sig, main_sig):
    """Return (ogg_bytes, suffix_len, tier). Prefer an exper donor; fall back to a main-song donor."""
    tail = transform_505(sdm_bytes[513:])
    p2 = tail.find(b"OggS")
    if p2 < 0:
        return None, 0, None
    setup_tail = tail[:p2]                        # this song's Page1 tail (mostly its SETUP/codebook)
    for tier, idx in (("exper", exper_sig), ("main", main_sig)):
        best = _best_donor(setup_tail, idx)
        if best and best[1] >= MIN_SUFFIX:
            return fix_ogg_serials(best[0] + tail[p2:]), best[1], tier
    b = _best_donor(setup_tail, exper_sig) or _best_donor(setup_tail, main_sig)
    return None, (b[1] if b else 0), None


def find_sdm(fid):
    for d in SDM_SRCS:
        p = os.path.join(d, f"{fid}.sdm")
        if ne(p) and os.path.getsize(p) > 514:
            return p
    return None


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    exper_sig, main_sig = build_donor_index()
    print(f"donor library: exper codebooks={len(exper_sig)}  main-song codebooks={len(main_sig)} (sig={SIG}B)")

    # 一首歌只解一次試聽:k/t 兩列指到同一個 fileId(同一個音檔),取 k 那列就好。
    fids = [r["fileId"] for r in song_table.load() if song_table.is_primary(r["gn"]) and r["fileId"]]
    os.makedirs(EXPER, exist_ok=True)
    for f in glob.glob(os.path.join(EXPER, "*.ogg")):
        os.remove(f)

    tmp = os.path.join(EXPER, "_tmp.ogg")
    ok = nosdm = nodonor = badstruct = 0
    tiers = {"exper": 0, "main": 0}
    unrec = []
    for i, fid in enumerate(fids, 1):
        sdm = find_sdm(fid)
        if not sdm:
            nosdm += 1; unrec.append((fid, "no-sdm")); continue
        out, cs, tier = decode(open(sdm, "rb").read(), exper_sig, main_sig)
        if out is None:
            nodonor += 1; unrec.append((fid, f"no-codebook-donor(bestSuffix={cs})")); continue
        open(tmp, "wb").write(out)
        if valid(tmp):
            os.replace(tmp, os.path.join(EXPER, f"{fid}.ogg")); ok += 1; tiers[tier] += 1
        else:
            badstruct += 1; unrec.append((fid, f"ffprobe-invalid(tier={tier})"))
        if i % 100 == 0 or i == len(fids):
            print(f"  [{i}/{len(fids)}] ok={ok} no-sdm={nosdm} no-donor={nodonor} bad={badstruct}")
    if os.path.isfile(tmp):
        os.remove(tmp)
    print(f"DONE: decoded {ok} (exper-donor {tiers['exper']}, main-donor {tiers['main']})  "
          f"no-sdm {nosdm}  no-codebook-donor {nodonor}  bad {badstruct}  / {len(fids)} songs")
    if unrec:
        print("未能正確解碼(需其它來源 donor):")
        for fid, why in unrec[:40]:
            print(f"  {fid}: {why}")


if __name__ == "__main__":
    main()
