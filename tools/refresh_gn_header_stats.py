# -*- coding: utf-8 -*-
"""
refresh_gn_header_stats — 就地刷新 gn_header_catalog.json 的「譜面數值」，但**保留歌名**。

用途:
  build_gn_header_catalog.py 是整份重寫，會連 title / artist（可能已被手動校正）一起蓋掉。
  add_songs_incremental.py 只加新歌、完全不碰既有筆。
  這支補中間那一塊：只針對「已經在 catalog 裡的歌」重讀 .gn 表頭，把數值欄位刷新成最新，
  title / artist（歌名+歌手）維持不動。不加歌、不刪歌，順序不變。

會刷新（重讀表頭覆蓋）:
  fileId, fileType, mode, bpm, levels, noteCounts, measurements, durations, producer, origName
會保留（沿用 catalog 舊值）:
  title, artist

來源與正確性:
  對每個 .gn **重新偵測**表頭位置/金鑰（gn_keytable.process_file），不信任任何舊 offset —
  檔案被換過(innerOff/seed 位移)也不會讀成亂碼。解不出金鑰、或表頭 fileType 不是 gn 的檔
  一律跳過、保留舊值，絕不把垃圾寫進 catalog。music 目錄沒有的歌 → 原樣保留並記為 skipped。

用法:
  python tools/refresh_gn_header_stats.py --dry-run      # 只看會改幾首、改了什麼，不寫檔
  python tools/refresh_gn_header_stats.py                # 就地刷新
  python tools/refresh_gn_header_stats.py --music <dir> --keytable <kt.json> --catalog <cat.json>
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Dict, List

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from gn_keytable import STEPFILE_HEADER, process_file  # noqa: E402
from build_gn_header_catalog import header_bytes, parse_header  # noqa: E402

# process_file 成功解碼的 enc（sdom_failed / None 代表沒解出，不可信）
GOOD_ENC = {"sdom", "ddrm", "plain", "rewu"}

REPO = HERE.parent
SA = REPO / "65" / "My project" / "Assets" / "StreamingAssets"
DEFAULT_CATALOG = SA / "gn_header_catalog.json"


def default_music() -> Path:
    """優先用 data_root.txt(遊戲實際載入的 DATA 根)下的 MUSIC；沒有才退回 online 子集。"""
    drt = REPO / "data_root.txt"
    if drt.is_file():
        root = drt.read_text(encoding="utf-8").strip()
        if root:
            m = Path(root) / "MUSIC"
            if m.is_dir():
                return m
    return REPO / "assets" / "閉撰敃氪" / "music"

# 重讀表頭後要覆蓋的欄位；title/artist 不在其中 → 永遠沿用舊值。
REFRESH_FIELDS = (
    "fileId", "fileType", "mode", "bpm",
    "levels", "noteCounts", "measurements", "durations",
    "producer", "origName",
)


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="就地刷新 gn_header_catalog 數值，保留歌名")
    ap.add_argument("--music", default=str(default_music()), help="music 目錄(含 .gn)；預設讀 data_root.txt 的 DATA/MUSIC")
    ap.add_argument("--catalog", default=str(DEFAULT_CATALOG), help="gn_header_catalog.json(就地更新)")
    ap.add_argument("--dry-run", action="store_true", help="只報告不寫檔")
    args = ap.parse_args()

    music = Path(args.music)
    cat_path = Path(args.catalog)
    if not music.is_dir():
        print(f"找不到 music 目錄: {music}", file=sys.stderr); return 1
    if not cat_path.is_file():
        print(f"找不到 catalog: {cat_path}", file=sys.stderr); return 1

    cat = json.loads(cat_path.read_text(encoding="utf-8"))
    songs: List[Dict[str, Any]] = cat.get("songs", [])

    seed_cache: Dict[bytes, int] = {}
    changed = 0
    changed_fields: Dict[str, int] = {}
    skipped: List[str] = []
    examples: List[str] = []

    for entry in songs:
        gn = entry.get("gn", "")
        fp = music / gn
        if not fp.is_file():
            skipped.append(f"{gn}(檔案不存在)"); continue
        try:
            raw = fp.read_bytes()
            song = process_file(raw, fp.name, seed_cache)  # 重新偵測 enc/offset/seed
            song["gn"] = gn
            if song.get("enc") not in GOOD_ENC:
                skipped.append(f"{gn}(解不出:{song.get('enc')})"); continue
            h = header_bytes(raw, song)
            if h is None or len(h) < STEPFILE_HEADER:
                skipped.append(f"{gn}(表頭取不到)"); continue
            fresh = parse_header(h, song)
            if fresh.get("fileType") not in ("gn", "GN"):
                skipped.append(f"{gn}(fileType={fresh.get('fileType')!r})"); continue
        except Exception as e:
            skipped.append(f"{gn}({e})"); continue

        diffs = []
        for f in REFRESH_FIELDS:
            if f in fresh and entry.get(f) != fresh[f]:
                diffs.append(f)
        if not diffs:
            continue
        changed += 1
        for f in diffs:
            changed_fields[f] = changed_fields.get(f, 0) + 1
        if len(examples) < 6:
            ex = ", ".join(f"{f}:{entry.get(f)}→{fresh[f]}" for f in diffs)
            examples.append(f"{gn}  {ex}")
        if not args.dry_run:
            for f in REFRESH_FIELDS:
                if f in fresh:
                    entry[f] = fresh[f]
            # title / artist 刻意不動

    if not args.dry_run and changed:
        cat["count"] = len(songs)
        cat["statsRefreshedBy"] = "tools/refresh_gn_header_stats.py"
        cat_path.write_text(json.dumps(cat, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")

    verb = "會改" if args.dry_run else "已改"
    print(f"{'[dry-run] ' if args.dry_run else ''}掃 {len(songs)} 首；{verb} {changed} 首；略過 {len(skipped)}（title/artist 全程保留）")
    if changed_fields:
        print("  各欄位變動筆數：" + "  ".join(f"{k}={v}" for k, v in sorted(changed_fields.items())))
    for ex in examples:
        print(f"    {ex}")
    if skipped[:5]:
        print(f"  略過樣本：{skipped[:5]}")
    if args.dry_run and changed:
        print("  → 確認無誤後拿掉 --dry-run 就地寫入。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
