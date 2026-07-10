# -*- coding: utf-8 -*-
"""
sync_songs_from_csv.py — 用「一份歌單 CSV」管理遊戲有哪些歌:把某首從 CSV 刪掉一列 → apply 就把它移除。

流程:
  1) export : 把目前遊戲的歌匯出成 game_song_roster.csv(gn, fileId, title, artist)
  2) 用 Excel 打開,把「不要的歌那一列」整列刪掉、存檔(只刪列,別動 gn 欄)
  3) apply  : 凡是「在遊戲(song_catalog)裡、但 CSV 已經沒有」的歌 → 徹底移除
              (刪譜面/音樂/試聽/DANCE/icon + 清 4 張目錄表,同 remove_songs)

安全:
  - apply 預設是「預覽」(只列出會移除哪些,不動手);確認無誤再加 --yes 真的移除。
  - 若要移除的數量異常多(可能 CSV 被截斷/存錯),會擋下來要你加 --force 才做,避免整批誤刪。

用法:
  python tools/sync_songs_from_csv.py export
  python tools/sync_songs_from_csv.py apply            # 預覽會移除哪些
  python tools/sync_songs_from_csv.py apply --yes      # 真的移除
"""
from __future__ import annotations

import argparse
import csv
import io
import json
import sys
from pathlib import Path
from typing import Dict, Set

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from remove_songs import load_catalog, remove_stems, stem, SA, OVERRIDES_JSON  # noqa: E402

ROSTER = HERE / "kgn_export" / "game_song_roster.csv"
COLS = ["gn", "fileId", "title", "artist"]


def overrides_by_stem() -> Dict[str, Dict]:
    if not OVERRIDES_JSON.is_file():
        return {}
    out = {}
    for e in json.loads(OVERRIDES_JSON.read_text(encoding="utf-8")).get("songs", []):
        if e.get("gn"):
            out[stem(e["gn"])] = e
    return out


def export(path: Path) -> int:
    by_stem = load_catalog()                     # K 譜代表(fileId/title 取 K)
    ov = overrides_by_stem()
    rows = []
    for s, v in by_stem.items():
        o = ov.get(s, {})
        rows.append({"gn": s, "fileId": v["fileId"],
                     "title": o.get("title") or v["title"], "artist": o.get("artist", "")})
    rows.sort(key=lambda r: (-int(r["fileId"] or 0), r["gn"]))   # 同遊戲:編號大在前
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=COLS); w.writeheader(); w.writerows(rows)
    print(f"匯出 {len(rows)} 首 -> {path}")
    print("  用 Excel 打開,把不要的歌那一整列刪掉存檔(別動 gn 欄),再跑: python tools/sync_songs_from_csv.py apply")
    return 0


def _read_gn_set(path: Path) -> Set[str]:
    raw = path.read_bytes()
    txt = raw.decode("utf-8-sig") if raw[:3] == b"\xef\xbb\xbf" else raw.decode("utf-8", "replace")
    out = set()
    for r in csv.DictReader(io.StringIO(txt)):
        g = (r.get("gn") or "").strip()
        if g:
            out.add(stem(g))
    return out


def apply(path: Path, do_it: bool, force: bool) -> int:
    if not path.is_file():
        print(f"找不到 {path}(先 export)", file=sys.stderr); return 1
    by_stem = load_catalog()
    catalog_stems = set(by_stem.keys())
    roster_stems = _read_gn_set(path)

    unknown = roster_stems - catalog_stems         # CSV 裡有、但遊戲已無(通常是先前已移除),忽略
    to_remove = catalog_stems - roster_stems       # 遊戲有、CSV 沒 → 要移除
    if not to_remove:
        print("沒有要移除的歌(CSV 與遊戲歌單一致)。"); return 0

    print(f"遊戲現有 {len(catalog_stems)} 首;CSV 保留 {len(roster_stems & catalog_stems)} 首;"
          f"將移除 {len(to_remove)} 首" + (f"(CSV 另有 {len(unknown)} 個不在遊戲的 gn,略過)" if unknown else ""))
    for s in sorted(to_remove)[:60]:
        v = by_stem[s]
        print(f"  - {s}  id={v['fileId']}  {v['title']!r}")
    if len(to_remove) > 60:
        print(f"  …(還有 {len(to_remove) - 60} 首)")

    # 安全閘:一次移除太多,八成是 CSV 存錯/被截斷
    big = len(to_remove) > max(100, len(catalog_stems) // 2)
    if big and not force:
        print(f"\n[擋下] 要移除 {len(to_remove)} 首(佔比很高),疑似 CSV 有誤。確定的話加 --force。", file=sys.stderr)
        return 2
    if not do_it:
        print("\n[預覽] 以上是「會被移除」的歌。確認無誤後加 --yes 真的移除。")
        return 0

    remove_stems(to_remove, by_stem, dry_run=False, keep_files=False)
    return 0


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="用歌單 CSV 增減遊戲歌曲(刪列=移除)")
    ap.add_argument("mode", choices=["export", "apply"])
    ap.add_argument("-c", "--csv", default=str(ROSTER))
    ap.add_argument("--yes", action="store_true", help="apply 時真的移除(預設只預覽)")
    ap.add_argument("--force", action="store_true", help="移除數量異常多時仍強制執行")
    args = ap.parse_args()
    p = Path(args.csv)
    return export(p) if args.mode == "export" else apply(p, args.yes, args.force)


if __name__ == "__main__":
    raise SystemExit(main())
