# -*- coding: utf-8 -*-
"""
build_kgn_export.py — 掃描三個 music 資料夾的 k.gn，整理出兩張 CSV：

  1) k_gn_keys.csv     — 每個 k.gn 的解密金鑰(seed/enc…)
  2) k_gn_songlist.csv — 每首歌的歌名/歌手（song_name_overrides.csv 同格式）

規則（照使用者指定）:
  - 依「資料夾優先順序」逐一解：閉撰敃氪 → SDO-X Alchemist → Super Dance Online。
  - 去重 key = gn 詞幹(sdomNNNN，去 K.gn)。**先出現的優先**：
      * 同詞幹在較前資料夾已收 → 後面的跳過。
      * 詞幹已存在於原本的 song_name_overrides（csv/json，660 首）→ 直接跳過（不重複收）。
  - 只處理 *K.gn（同首 K/T 共用一筆，K 譜即代表）。

歌名編碼:
  k.gn 內嵌 StepFile 表頭的歌名/歌手是 GBK(簡中)；本工具用 gb18030 解成 UTF-8，
  再以 opencc s2twp 由簡轉繁(台灣用字)。英文/日文/已是繁體者不動（沿用 build_song_name_overrides.to_traditional）。

用法:
  python tools/build_kgn_export.py
  python tools/build_kgn_export.py -o tools/kgn_export
"""
from __future__ import annotations

import argparse
import csv
import json
import struct
import sys
from pathlib import Path
from typing import Dict, List, Set

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
sys.path.insert(0, str(HERE))

import gn_keytable as G                                   # process_file / lcg_transform / decrypt_ddrm
from build_song_name_overrides import to_traditional, decode_gb, stem  # 沿用專案既有轉繁/解碼/詞幹

SA = REPO / "65" / "My project" / "Assets" / "StreamingAssets"
OVERRIDES_CSV = SA / "song_name_overrides.csv"
OVERRIDES_JSON = SA / "song_name_overrides.json"

# 資料夾優先順序（越前面越優先；同詞幹先出現者勝）
FOLDERS = [
    ("閉撰敃氪", Path(r"H:\65_remake\assets\閉撰敃氪\music")),
    ("SDO-X Alchemist", Path(r"D:\program\SDO-X\SDO-X Alchemist World\music")),
    ("Super Dance Online", Path(r"H:\sdo\Super Dance Online\music")),
]

KEYS_COLS = ["gn", "file", "source", "enc", "seed", "innerOff", "seed1", "seed2", "fileId", "bpm"]
SONG_COLS = ["gn", "title", "artist", "src", "bpm", "fileId"]   # 對齊 song_name_overrides.csv


def load_override_stems() -> Set[str]:
    """原本 song_name_overrides 的 gn 詞幹集合（csv ∪ json）；這些一律跳過。"""
    seen: Set[str] = set()
    if OVERRIDES_CSV.is_file():
        raw = OVERRIDES_CSV.read_bytes()
        txt = raw.decode("utf-8-sig") if raw[:3] == b"\xef\xbb\xbf" else raw.decode("utf-8", "replace")
        import io
        for r in csv.DictReader(io.StringIO(txt)):
            g = (r.get("gn") or "").strip()
            if g:
                seen.add(stem(g))
    if OVERRIDES_JSON.is_file():
        try:
            for r in json.loads(OVERRIDES_JSON.read_text(encoding="utf-8")).get("songs", []):
                if r.get("gn"):
                    seen.add(stem(r["gn"]))
        except Exception as e:
            print(f"[warn] 讀 song_name_overrides.json 失敗：{e}", file=sys.stderr)
    return seen


def header_bytes(raw: bytes, entry: Dict) -> bytes | None:
    """依 enc 取出 300-byte StepFile 表頭（明文）。取不到回 None。"""
    enc = entry.get("enc")
    if enc == "sdom":
        off = int(entry.get("innerOff", 0))
        return raw[off:off + G.STEPFILE_HEADER]
    if enc == "rewu":
        return G.lcg_transform(int(entry["seed"]), raw[:G.STEPFILE_HEADER], False)
    if enc == "ddrm":
        dec = G.decrypt_ddrm(raw)
        return dec[0][:G.STEPFILE_HEADER] if dec else None
    if enc == "plain":
        return raw[:G.STEPFILE_HEADER]
    return None


def collect_k_gn(folder: Path) -> List[Path]:
    if not folder.is_dir():
        return []
    return sorted(p for p in folder.iterdir()
                  if p.is_file() and p.name.lower().endswith("k.gn") and p.stat().st_size > 0)


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="掃 k.gn → 金鑰 CSV + 歌名 CSV")
    ap.add_argument("-o", "--outdir", default=str(HERE / "kgn_export"))
    args = ap.parse_args()
    outdir = Path(args.outdir)
    outdir.mkdir(parents=True, exist_ok=True)

    override_stems: Set[str] = load_override_stems()
    seen: Set[str] = set(override_stems)
    print(f"原本 song_name_overrides 已收 {len(override_stems)} 首詞幹 → 這些一律跳過")

    seed_cache: Dict[bytes, int] = {}
    key_rows: List[Dict] = []
    song_rows: List[Dict] = []
    stats = {"total_k": 0, "added": 0, "skip_override": 0, "skip_dupfolder": 0,
             "no_header": 0, "by_enc": {}}

    for label, folder in FOLDERS:
        files = collect_k_gn(folder)
        print(f"\n=== {label} === {folder}\n  k.gn={len(files)}")
        added_here = 0
        for p in files:
            stats["total_k"] += 1
            s = stem(p.name)                       # sdomNNNN
            if s in seen:
                # 分辨是被原始 overrides 擋掉、還是被前面資料夾擋掉
                stats["skip_override" if s in override_stems else "skip_dupfolder"] += 1
                continue
            try:
                raw = p.read_bytes()
                entry = G.process_file(raw, p.name, seed_cache)
            except Exception as e:
                entry = {"enc": "error", "error": str(e)}
                raw = b""
            enc = entry.get("enc", "unknown")
            stats["by_enc"][enc] = stats["by_enc"].get(enc, 0) + 1

            key_rows.append({
                "gn": s,
                "file": p.name,
                "source": label,
                "enc": enc,
                "seed": entry.get("seed", ""),
                "innerOff": entry.get("innerOff", ""),
                "seed1": entry.get("seed1", ""),
                "seed2": entry.get("seed2", ""),
                "fileId": entry.get("fileId", ""),
                "bpm": entry.get("bpm", ""),
            })

            hdr = header_bytes(raw, entry) if raw else None
            if hdr is not None and len(hdr) >= G.STEPFILE_HEADER:
                title = to_traditional(decode_gb(hdr[108:140]))
                artist = to_traditional(decode_gb(hdr[172:204]))
                song_rows.append({
                    "gn": s,
                    "title": title,
                    "artist": artist,
                    "src": "kgn",
                    "bpm": entry.get("bpm", ""),
                    "fileId": entry.get("fileId", ""),
                })
            else:
                stats["no_header"] += 1

            seen.add(s)
            stats["added"] += 1
            added_here += 1
        print(f"  新增 {added_here} 首")

    keys_path = outdir / "k_gn_keys.csv"
    song_path = outdir / "k_gn_songlist.csv"
    with keys_path.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=KEYS_COLS, extrasaction="ignore")
        w.writeheader()
        w.writerows(key_rows)
    with song_path.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=SONG_COLS, extrasaction="ignore")
        w.writeheader()
        w.writerows(song_rows)

    print("\n---- 完成 ----")
    print(f"  掃描 k.gn 共 {stats['total_k']}；新增 {stats['added']}；"
          f"跳過(已在 overrides 或前資料夾) {stats['total_k'] - stats['added']}")
    print(f"  enc 分佈：{stats['by_enc']}；無表頭(未寫入 songlist) {stats['no_header']}")
    print(f"  金鑰表 → {keys_path}  ({len(key_rows)} 列)")
    print(f"  歌  單 → {song_path}  ({len(song_rows)} 列)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
