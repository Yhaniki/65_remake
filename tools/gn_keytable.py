# -*- coding: utf-8 -*-
"""
gn_keytable — 預先把 music 目錄下所有 .gn 的解密金鑰(seed)算好，寫進 song_table.csv 的解密欄位。

動機:
  SDOM 系 .gn（馬來西亞單機版，magic 'sdom'）的 LCG seed 不存在檔頭，必須以
  已知明文暴力還原，純 Python 每檔約 5 秒。歌曲有兩千多首，逐檔即時還原太慢。
  本工具一次把所有 seed 算好寫成表；遊戲端只要讀這張表，即可用存好的 seed 直接
  LCG 解密（毫秒級），免再暴力搜尋。

加速:
  以 numpy 向量化 keystream 還原，單檔 ~5s → ~0.14s（約 36×）。
  再以 keystream 指紋 (前 16 bytes) 建快取：相同 keystream 的重複檔/共用 seed 檔
  直接命中，免重算。

支援的 .gn 類型(由解密引擎自動判別，對齊 H:/bms/tools/bms_sdo/gn_crypto.py):
  ddrm  : DDRM (SDO Online)        — seed1/seed2 存在檔頭，瞬間取得
  sdom  : SDOM (馬來西亞單機版)     — 單一 LCG seed，需還原(本工具的重點)
  plain : 未加密，開頭即標準 StepFile
  rewu  : 熱舞 Online 整檔 LCG      — 全檔 LCG 加密、無明文前綴；以 raw[4:8]='gn\0\0' 已知明文還原 seed

輸出（併進 StreamingAssets/song_table.csv 的對應列，見 tools/song_table.py）:
  enc       : 上述類型
  seed      : (sdom) LCG seed；遊戲端 body = lcg_decrypt(seed, raw[inner_off+300:])
  inner_off : (sdom) 內嵌 StepFile 在檔內的位移(本資料夾恆為 456)
  seed1,seed2: (ddrm) 兩段 LCG seed
  mode      : 由檔名推得的譜面型別 K / T（其餘為空字串）
  size,file_id,bpm,title : 便於當作歌單清單用的輕量 metadata（解析失敗則省略）

用法:
  python tools/gn_keytable.py "H:/65_remake/assets/<music 目錄>"
  python tools/gn_keytable.py <root> -o <out.csv> [--recursive] [--limit N]
"""
from __future__ import annotations

import argparse
import json
import os
import struct
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import song_table as st  # noqa: E402

# ---------------------------------------------------------------------------
# 最小加解密核心（內聯，faithful to bms_sdo/gn_crypto.py，避免跨倉相依絕對路徑）
# ---------------------------------------------------------------------------
MAGIC_DDRM = 0x6D726464          # 'drmd' LE — DDRM 容器
MULTIPLIER = 0x3D09              # LCG 乘數 (=5^6=15625)
INV_MULT = pow(MULTIPLIER, -1, 2 ** 32)
HEADER_SIZE = 0x54               # DDRM 標頭長
BLOCK1_LEN = 0x20
OFF_SEED1 = 0x0C
OFF_BLOCK1 = 0x20
B1_OFF_SEED2 = 0x04
STEPFILE_HEADER = 300
OFF_FILE_TYPE = 4
OFF_ADDRESS_EASY = 284


def lcg_transform(seed: int, data: bytes, encrypt: bool = False) -> bytes:
    """LCG keystream 加/解密；keystream byte = (state>>16)&0xFF，每步 state*=MULT。"""
    state = seed & 0xFFFFFFFF
    out = bytearray(data)
    for i in range(len(out)):
        state = (state * MULTIPLIER) & 0xFFFFFFFF
        k = (state >> 16) & 0xFF
        out[i] = (out[i] + k) & 0xFF if encrypt else (out[i] - k) & 0xFF
    return bytes(out)


def find_sdom_gn_inner_offset(raw: bytes, scan_max: int = 0x4000) -> Optional[int]:
    """SDOM .gn：檔首為資源檔名表等明文，內嵌 StepFile。掃描第一個 gn+address_easy==300。"""
    n = len(raw)
    if n < STEPFILE_HEADER + 8:
        return None
    limit = min(scan_max, n - STEPFILE_HEADER)
    for off in range(0, limit):
        if raw[off + OFF_FILE_TYPE: off + OFF_FILE_TYPE + 2] != b"gn":
            continue
        if raw[off + OFF_FILE_TYPE + 2: off + OFF_FILE_TYPE + 4] != b"\x00\x00":
            continue
        ae, an, ah, aend = struct.unpack_from("<IIII", raw, off + OFF_ADDRESS_EASY)
        if ae != 300:
            continue
        if not (300 <= an <= ah <= aend <= n - off):
            continue
        return off
    return None


def decrypt_ddrm(data: bytes) -> Optional[Tuple[bytes, int, int]]:
    """DDRM 解密，回傳 (body_pt, seed1, seed2)；seed 取自檔頭，瞬間完成。"""
    if len(data) < HEADER_SIZE:
        return None
    s1 = struct.unpack_from("<I", data, OFF_SEED1)[0]
    block1_pt = lcg_transform(s1, data[OFF_BLOCK1: OFF_BLOCK1 + BLOCK1_LEN], encrypt=False)
    s2 = struct.unpack_from("<I", block1_pt, B1_OFF_SEED2)[0]
    body_pt = lcg_transform(s2, data[HEADER_SIZE:], encrypt=False)
    return body_pt, s1, s2


# ---- numpy 向量化 SDOM seed 還原 ------------------------------------------
_MULT = np.uint32(MULTIPLIER)
_M16 = np.uint32(16)
_MFF = np.uint32(0xFF)
_INV = np.uint64(INV_MULT)
_MASK32 = np.uint64(0xFFFFFFFF)


def recover_sdom_seeds_np(header: bytes, encrypted: bytes, check_bytes: int = 16) -> List[int]:
    """以已知明文(header[:n] == decrypt(encrypted[:n]))向量化還原 LCG seed 候選。

    keystream 只取 state 的 bit16..23，僅依賴 state 低 24 bits；故恆有 256 個等價
    seed（差在最高 8 bit），全部對整檔產生相同明文，取任一即可。
    """
    n = min(check_bytes, len(header), len(encrypted))
    if n < 4:
        return []
    ks = [(encrypted[i] - header[i]) & 0xFF for i in range(n)]
    lo = np.arange(65536, dtype=np.uint32)
    hi = np.arange(256, dtype=np.uint32) << np.uint32(24)
    states = (hi[:, None] | (np.uint32(ks[0]) << _M16) | lo[None, :]).reshape(-1)
    sc = states * _MULT
    m = ((sc >> _M16) & _MFF) == np.uint32(ks[1])
    cand, sc = states[m], sc[m]
    for i in range(2, n):
        sc = sc * _MULT
        keep = ((sc >> _M16) & _MFF) == np.uint32(ks[i])
        cand, sc = cand[keep], sc[keep]
        if cand.size == 0:
            break
    seeds = ((cand.astype(np.uint64) * _INV) & _MASK32).astype(np.uint32)
    return [int(x) for x in seeds]


def recover_rewu_seed_np(raw: bytes) -> Optional[int]:
    """熱舞 Online .gn「整檔 LCG」加密的 seed 還原（向量化）。

    這類檔整檔以 LCG(0x3D09) 加密、無 SDOM 的明文資源前綴，故 find_sdom_gn_inner_offset
    會失敗。唯一已知明文是 StepFile 的 file_type 欄：raw[4:8] ∈ {'gn\\x00\\x00','GN\\x00\\x00'}。
    以此 4 個連續 keystream byte 反推 LCG 狀態；因 keystream 錨在「輸出 byte 4」，候選狀態
    = seed*MULT^5，需回推 5 步得 seed。與 SDOM 相同，keystream 僅依賴 state 低 24 bits，
    故每組 keystream 恰有 256 個等價 seed，取最小者當代表。回傳驗證通過的 seed 或 None。
    """
    if len(raw) < STEPFILE_HEADER:
        return None
    for expected in (b"gn\x00\x00", b"GN\x00\x00"):
        ks = [(raw[4 + i] - expected[i]) & 0xFF for i in range(4)]
        lo = np.arange(65536, dtype=np.uint32)
        hi = np.arange(256, dtype=np.uint32) << np.uint32(24)
        states = (hi[:, None] | (np.uint32(ks[0]) << _M16) | lo[None, :]).reshape(-1)
        sc = states * _MULT
        m = ((sc >> _M16) & _MFF) == np.uint32(ks[1])
        cand, sc = states[m], sc[m]
        for j in (2, 3):
            sc = sc * _MULT
            keep = ((sc >> _M16) & _MFF) == np.uint32(ks[j])
            cand, sc = cand[keep], sc[keep]
            if cand.size == 0:
                break
        if cand.size == 0:
            continue
        seeds = cand.astype(np.uint64)
        for _ in range(5):                       # keystream 錨在 byte 4 → 回推 5 步
            seeds = (seeds * _INV) & _MASK32
        for s in sorted({int(x) for x in seeds.astype(np.uint32)}):
            dec = lcg_transform(s, raw[:STEPFILE_HEADER], encrypt=False)
            if dec[4:8] == expected and struct.unpack_from("<I", dec, OFF_ADDRESS_EASY)[0] == 300:
                return s
    return None


# ---------------------------------------------------------------------------
# 輕量 metadata（不需完整 StepFileBin；失敗就略過，不阻斷建表）
# ---------------------------------------------------------------------------
def stepfile_meta(stepfile_header: bytes) -> Dict[str, Any]:
    meta: Dict[str, Any] = {}
    if len(stepfile_header) < STEPFILE_HEADER:
        return meta
    try:
        meta["fileId"] = struct.unpack_from("<I", stepfile_header, 0)[0]
        bpm = struct.unpack_from("<f", stepfile_header, 16)[0]
        if bpm == bpm and abs(bpm) < 1e6:  # 非 NaN、合理範圍
            meta["bpm"] = round(float(bpm), 4)
        title = stepfile_header[108:140].split(b"\x00", 1)[0]
        if title:
            # SDO 中國/馬來西亞版字串為 GBK(簡中)；少數可能 UTF-8/Big5
            for enc in ("gbk", "utf-8", "big5"):
                try:
                    t = title.decode(enc).strip()
                    if t:
                        meta["title"] = t
                        break
                except Exception:
                    continue
    except Exception:
        pass
    return meta


def mode_from_name(name: str) -> str:
    stem = name[:-3] if name.lower().endswith(".gn") else name
    last = stem[-1:] if stem else ""
    return last.upper() if last in ("K", "k", "T", "t") else ""


# ---------------------------------------------------------------------------
# 單檔處理
# ---------------------------------------------------------------------------
def process_file(raw: bytes, name: str, seed_cache: Dict[bytes, int]) -> Dict[str, Any]:
    """回傳該檔的金鑰表項目（含 enc/seed/...）；seed_cache 以 keystream 指紋加速。"""
    entry: Dict[str, Any] = {"size": len(raw)}
    mode = mode_from_name(name)
    if mode:
        entry["mode"] = mode

    # 1) DDRM
    if len(raw) >= HEADER_SIZE and struct.unpack_from("<I", raw, 0)[0] == MAGIC_DDRM:
        dec = decrypt_ddrm(raw)
        if dec and dec[0][OFF_FILE_TYPE:OFF_FILE_TYPE + 2] in (b"gn", b"GN"):
            body, s1, s2 = dec
            entry.update(enc="ddrm", seed1=s1, seed2=s2)
            entry.update(stepfile_meta(body[:STEPFILE_HEADER]))
            return entry

    # 2) plain（開頭即 StepFile）
    if (len(raw) >= STEPFILE_HEADER
            and raw[OFF_FILE_TYPE:OFF_FILE_TYPE + 2] in (b"gn", b"GN")
            and raw[OFF_FILE_TYPE + 2:OFF_FILE_TYPE + 4] == b"\x00\x00"
            and struct.unpack_from("<I", raw, OFF_ADDRESS_EASY)[0] == 300):
        entry.update(enc="plain")
        entry.update(stepfile_meta(raw[:STEPFILE_HEADER]))
        return entry

    # 3) SDOM（重點：還原 seed）
    off = find_sdom_gn_inner_offset(raw)
    if off is not None:
        inner = raw[off:]
        header = inner[:STEPFILE_HEADER]
        enc = inner[STEPFILE_HEADER:]
        if len(enc) >= 16:
            fp = bytes((enc[i] - header[i]) & 0xFF for i in range(16))  # keystream 指紋
            seed = seed_cache.get(fp)
            if seed is None or lcg_transform(seed, enc[:STEPFILE_HEADER], False) != header:
                cands = recover_sdom_seeds_np(header, enc)
                seed = None
                for c in sorted(cands):  # 取最小值當代表，跨次執行可重現
                    if lcg_transform(c, enc[:STEPFILE_HEADER], False) == header:
                        seed = c
                        break
                if seed is not None:
                    seed_cache[fp] = seed
            if seed is not None:
                entry.update(enc="sdom", seed=seed, innerOff=off)
                entry.update(stepfile_meta(header))
                return entry
        entry.update(enc="sdom_failed", innerOff=off)
        return entry

    # 4) rewu（熱舞 Online 整檔 LCG）— 無明文前綴，故 SDOM 掃描失敗才走到這。
    #    以 raw[4:8] 當指紋快取 seed（相同 keystream 的檔共用），命中後驗證再用。
    fp = b"rewu:" + raw[4:8]
    seed = seed_cache.get(fp)
    if seed is not None:
        dec = lcg_transform(seed, raw[:STEPFILE_HEADER], False)
        if dec[4:8] not in (b"gn\x00\x00", b"GN\x00\x00") or struct.unpack_from("<I", dec, OFF_ADDRESS_EASY)[0] != 300:
            seed = None
    if seed is None:
        seed = recover_rewu_seed_np(raw)
        if seed is not None:
            seed_cache[fp] = seed
    if seed is not None:
        body = lcg_transform(seed, raw, encrypt=False)
        entry.update(enc="rewu", seed=seed)
        entry.update(stepfile_meta(body[:STEPFILE_HEADER]))
        return entry

    # 5) 其他（未知格式）
    entry.update(enc="unknown")
    return entry


def collect_gn_files(root: Path, recursive: bool) -> List[Path]:
    if recursive:
        return sorted(p for p in root.rglob("*") if p.suffix.lower() == ".gn" and p.is_file())
    return sorted(p for p in root.iterdir() if p.suffix.lower() == ".gn" and p.is_file())


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="算出每個 .gn 的解密 seed，併進 song_table.csv")
    ap.add_argument("root", help="music 目錄")
    ap.add_argument("-o", "--output", help="輸出 CSV（預設 StreamingAssets/song_table.csv）")
    ap.add_argument("--recursive", action="store_true", help="遞迴掃描子目錄（含 backup/test 等）")
    ap.add_argument("--limit", type=int, default=0, help="只處理前 N 個（測試用）")
    args = ap.parse_args()

    root = Path(args.root).resolve()
    if not root.is_dir():
        print(f"找不到目錄: {root}", file=sys.stderr)
        return 1
    out_path = Path(args.output).resolve() if args.output else st.DEFAULT_CSV

    files = collect_gn_files(root, args.recursive)
    if args.limit:
        files = files[:args.limit]
    total = len(files)
    print(f"掃描 {root}（recursive={args.recursive}）→ {total} 個 .gn")

    entries: Dict[str, Any] = {}
    counts: Dict[str, int] = {}
    seed_cache: Dict[bytes, int] = {}
    cache_hits = 0
    t0 = time.time()
    for i, p in enumerate(files, 1):
        key = p.name if not args.recursive else str(p.relative_to(root)).replace("\\", "/")
        try:
            raw = p.read_bytes()
            before = len(seed_cache)
            entry = process_file(raw, p.name, seed_cache)
            if entry.get("enc") == "sdom" and len(seed_cache) == before:
                cache_hits += 1
        except Exception as e:
            entry = {"enc": "error", "error": str(e), "size": p.stat().st_size}
        entries[key] = entry
        counts[entry.get("enc", "unknown")] = counts.get(entry.get("enc", "unknown"), 0) + 1
        if i % 100 == 0 or i == total:
            dt = time.time() - t0
            rate = i / dt if dt else 0
            print(f"  [{i}/{total}] {dt:.1f}s  {rate:.1f} 檔/s  快取命中={cache_hits}  "
                  f"unique_seed={len(seed_cache)}", flush=True)

    # 併進 song_table.csv：只動解密欄位（enc/seed/seed1/seed2/innerOff/size）＋新歌的基本欄，
    # 歌名/難度/offset 那些是別的工具（和人）的地盤，一個字都不碰。
    rows = st.by_gn(st.load(out_path))
    added = 0
    for name, entry in entries.items():
        gn = name.lower()
        row = rows.get(gn)
        if row is None:
            row = rows[gn] = st.blank_row(gn)
            added += 1
        for col in ("enc", "seed", "seed1", "seed2", "innerOff", "size"):
            row[col] = entry.get(col)
        if entry.get("mode") and not row.get("mode"):
            row["mode"] = entry["mode"]
        for col, key in (("fileId", "fileId"), ("chartBpm", "bpm")):   # 新歌的打底值
            if row.get(col) is None and entry.get(key) is not None:
                row[col] = entry[key]
        if not row.get("titleZhCn") and entry.get("title"):
            row["titleZhCn"] = entry["title"]

    st.save(rows.values(), out_path)
    dt = time.time() - t0
    print(f"完成：{out_path}（{len(rows)} 列，新增 {added}）")
    print(f"  耗時 {dt:.1f}s；類型統計 {dict(total=total, **counts)}")
    print(f"  unique seed={len(seed_cache)}，快取命中 {cache_hits} 檔（免重算）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
