# -*- coding: utf-8 -*-
"""
song_names_csv.py — 在「可編輯歌名清單」的 JSON 與 CSV 之間互轉，方便用 Excel 看/改。

遊戲實際讀的是 song_name_overrides.json；CSV 只是給人好讀好改的鏡像。流程：
  1) export：JSON -> CSV(用 Excel/試算表打開，改 title / artist 欄)
  2) apply ：改完的 CSV -> 寫回 JSON(遊戲即讀新名)

CSV 欄位：gn, title, artist, src, bpm, fileId
  - 要改名就改 title / artist 兩欄；bpm 欄也可改(覆蓋歌單/房間顯示的 BPM 數字，
    只影響顯示，遊戲判定/流速一律讀譜面本身)。留空 = 不動該首 bpm。
  - gn 是對應 key(gn 詞幹，例 sdom0001)，別動；fileId/src 僅供辨識。
  - 也可新增一列(填 gn+title+artist)來覆蓋清單原本沒有或不對的歌。
CSV 用 UTF-8-BOM 存(utf-8-sig)，Excel(繁中 Windows)才不會把中文顯示成亂碼。

用法:
  python tools/song_names_csv.py export         # JSON -> CSV
  python tools/song_names_csv.py apply           # CSV -> JSON(把 CSV 的 title/artist 寫回)
  python tools/song_names_csv.py export -j <json> -c <csv>
"""
from __future__ import annotations

import argparse
import csv
import io
import json
import sys
from pathlib import Path
from typing import Dict, List

HERE = Path(__file__).resolve().parent
SA = HERE.parent / "65" / "My project" / "Assets" / "StreamingAssets"
DEFAULT_JSON = SA / "song_name_overrides.json"
DEFAULT_CSV = SA / "song_name_overrides.csv"

COLS = ["gn", "title", "artist", "src", "bpm", "fileId"]


def export(json_path: Path, csv_path: Path) -> int:
    doc = json.loads(json_path.read_text(encoding="utf-8"))
    rows = doc.get("songs", [])
    with csv_path.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=COLS, extrasaction="ignore")
        w.writeheader()
        for r in rows:
            w.writerow({c: r.get(c, "") for c in COLS})
    print(f"匯出 {len(rows)} 首 -> {csv_path}")
    print("  用 Excel 打開改 title / artist，改完跑： python tools/song_names_csv.py apply")
    return 0


def _read_csv_text(csv_path: Path) -> str:
    """讀 CSV 文字，容忍 Excel 常見存法：UTF-8(BOM)、UTF-16、以及繁中 Windows 的 cp950/big5。"""
    raw = csv_path.read_bytes()
    if raw[:3] == b"\xef\xbb\xbf":
        return raw.decode("utf-8-sig")
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16")
    for enc in ("utf-8", "cp950", "big5hkscs", "gb18030"):
        try:
            return raw.decode(enc)
        except Exception:
            continue
    return raw.decode("utf-8", "replace")


def apply(csv_path: Path, json_path: Path) -> int:
    if not csv_path.is_file():
        print(f"找不到 CSV：{csv_path}（先 export）", file=sys.stderr); return 1
    doc = json.loads(json_path.read_text(encoding="utf-8")) if json_path.is_file() else {"schema": "song-name-overrides/2", "songs": []}
    songs: List[Dict] = doc.get("songs", [])
    by_gn: Dict[str, Dict] = {r["gn"]: r for r in songs if r.get("gn")}

    reader = csv.DictReader(io.StringIO(_read_csv_text(csv_path)))
    n_upd = n_add = n_same = 0
    for row in reader:
        gn = (row.get("gn") or "").strip()
        if not gn:
            continue
        title = (row.get("title") or "").strip()
        artist = (row.get("artist") or "").strip()
        bpm = round(float(row["bpm"]), 3) if _isfloat(row.get("bpm")) else None
        e = by_gn.get(gn)
        if e is None:                       # CSV 新增列 -> 新增到清單
            e = {"gn": gn,
                 "fileId": int(row["fileId"]) if (row.get("fileId") or "").strip().isdigit() else 0,
                 "bpm": bpm if bpm is not None else 0.0,
                 "src": (row.get("src") or "manual").strip() or "manual",
                 "title": title, "artist": artist}
            songs.append(e); by_gn[gn] = e; n_add += 1
            continue
        changed = e.get("title", "") != title or e.get("artist", "") != artist
        # bpm 也可改(只影響歌單/房間顯示的 BPM 數字，遊戲判定與流速仍讀譜面)。空白 = 不動。
        if bpm is not None and abs(float(e.get("bpm") or 0.0) - bpm) > 1e-6:
            e["bpm"] = bpm
            changed = True
        if not changed:
            n_same += 1
        else:
            e["title"] = title
            e["artist"] = artist
            if e.get("src") not in ("manual",):   # 標記此列被人改過(除非本來就 manual)
                e["src"] = "manual"
            n_upd += 1

    doc["count"] = len(songs)
    json_path.write_text(json.dumps(doc, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"寫回 {json_path}")
    print(f"  更新 {n_upd}、新增 {n_add}、未變 {n_same}（遊戲下次啟動即讀新名）")
    return 0


def _isfloat(s) -> bool:
    try:
        float(s); return True
    except Exception:
        return False


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="歌名清單 JSON <-> CSV 互轉")
    ap.add_argument("mode", choices=["export", "apply"], help="export=JSON→CSV, apply=CSV→JSON")
    ap.add_argument("-j", "--json", default=str(DEFAULT_JSON))
    ap.add_argument("-c", "--csv", default=str(DEFAULT_CSV))
    args = ap.parse_args()
    jp, cp = Path(args.json), Path(args.csv)
    if args.mode == "export":
        if not jp.is_file():
            print(f"找不到 JSON：{jp}（先跑 build_song_name_overrides.py）", file=sys.stderr); return 1
        return export(jp, cp)
    return apply(cp, jp)


if __name__ == "__main__":
    raise SystemExit(main())
