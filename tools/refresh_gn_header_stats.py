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
from build_song_catalog_from_gn import clamp_dur  # noqa: E402

# process_file 成功解碼的 enc（sdom_failed / None 代表沒解出，不可信）
GOOD_ENC = {"sdom", "ddrm", "plain", "rewu"}

REPO = HERE.parent
SA = REPO / "65" / "My project" / "Assets" / "StreamingAssets"
DEFAULT_CATALOG = SA / "gn_header_catalog.json"
DEFAULT_SONG_CATALOG = SA / "song_catalog.json"


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

# 重讀表頭後要覆蓋的欄位。刻意排除：
#   title/artist —— 歌名，永遠沿用舊值。
#   fileId       —— 可能是人工指定的「去撞號槽位」（重複歌 sdomNNNN_1k 借槽，如 1117_1=11138，
#                   而表頭仍寫原曲 11117）；refresh 會還原成表頭值造成撞號。絕不碰。
REFRESH_FIELDS = (
    "fileType", "mode", "bpm",
    "levels", "noteCounts", "measurements", "durations",
    "producer", "origName",
)


def sync_song_catalog(gn_songs: List[Dict[str, Any]], sc_path: Path, allow_gn: set, dry_run: bool):
    """把 gn_header 的數值同步到 song_catalog.json（選單真正讀的檔）。

    只動數值欄位(diff/notes/dur/bpm)；title/artist 及成員/順序全部保留——顯示歌名另由
    song_name_overrides.json 於 runtime 覆蓋，這裡不碰。

    只同步 allow_gn 內的歌 = 本次真的從 music 目錄(權威來源)成功解析過的；藉此排除
    T 譜(重製版 K-only、且不在 clean)與解不出的檔，不讓它們被舊/錯來源的值覆寫。
    回傳 (changed, examples)。
    """
    if not sc_path.is_file():
        print(f"  [warn] 找不到 song_catalog：{sc_path}，跳過同步", file=sys.stderr)
        return 0, []
    sc = json.loads(sc_path.read_text(encoding="utf-8"))
    rows: List[Dict[str, Any]] = sc.get("songs", [])
    by_gn = {s.get("gn", "").lower(): s for s in gn_songs
             if s.get("gn") and s["gn"].lower() in allow_gn}

    def want(e):
        lv = (e.get("levels") or [0, 0, 0]) + [0, 0, 0]
        nt = (e.get("noteCounts") or [0, 0, 0]) + [0, 0, 0]
        du = (e.get("durations") or [0, 0, 0]) + [0, 0, 0]
        return {
            "bpm": round(float(e.get("bpm", 0.0)), 3),
            "diffEasy": int(lv[0]), "diffNormal": int(lv[1]), "diffHard": int(lv[2]),
            "notesEasy": int(nt[0]), "notesNormal": int(nt[1]), "notesHard": int(nt[2]),
            "durEasy": clamp_dur(int(du[0])), "durNormal": clamp_dur(int(du[1])), "durHard": clamp_dur(int(du[2])),
        }

    changed = 0
    examples: List[str] = []
    for row in rows:
        e = by_gn.get(row.get("gn", "").lower())
        if e is None:
            continue
        w = want(e)
        diffs = [k for k, v in w.items() if row.get(k) != v]
        if not diffs:
            continue
        changed += 1
        if len(examples) < 4:
            examples.append(f"{row['gn']}  " + ", ".join(f"{k}:{row.get(k)}→{w[k]}" for k in diffs))
        if not dry_run:
            row.update(w)  # 只覆蓋數值鍵；title/artist/其它鍵不動
    if not dry_run and changed:
        sc_path.write_text(json.dumps({"songs": rows}, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    return changed, examples


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="就地刷新 gn_header_catalog 數值，保留歌名")
    ap.add_argument("--music", default=str(default_music()), help="music 目錄(含 .gn)；預設讀 data_root.txt 的 DATA/MUSIC")
    ap.add_argument("--catalog", default=str(DEFAULT_CATALOG), help="gn_header_catalog.json(就地更新)")
    ap.add_argument("--song-catalog", default=str(DEFAULT_SONG_CATALOG), help="song_catalog.json(選單讀的檔，同步數值)")
    ap.add_argument("--no-song-catalog", action="store_true", help="只更新 gn_header，不同步 song_catalog")
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
    parsed_ok: set = set()   # gns 本次從 music 目錄成功解析（song_catalog 只同步這些）

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
        parsed_ok.add(gn.lower())

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
        # 一律更新記憶體(讓下游 song_catalog 同步/預覽看到刷新後的值)；title/artist 刻意不動。
        # 是否寫檔另由 dry_run 把關(見下方)。
        for f in REFRESH_FIELDS:
            if f in fresh:
                entry[f] = fresh[f]

    if not args.dry_run and changed:
        cat["count"] = len(songs)
        cat["statsRefreshedBy"] = "tools/refresh_gn_header_stats.py"
        cat_path.write_text(json.dumps(cat, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")

    verb = "會改" if args.dry_run else "已改"
    print(f"{'[dry-run] ' if args.dry_run else ''}gn_header：掃 {len(songs)} 首；{verb} {changed} 首；略過 {len(skipped)}（title/artist 全程保留）")
    if changed_fields:
        print("  各欄位變動筆數：" + "  ".join(f"{k}={v}" for k, v in sorted(changed_fields.items())))
    for ex in examples:
        print(f"    {ex}")
    if skipped[:5]:
        print(f"  略過樣本：{skipped[:5]}")

    # 把數值同步到 song_catalog.json（選單真正讀的檔）；同樣只碰數值、保留歌名。
    if not args.no_song_catalog:
        sc_changed, sc_examples = sync_song_catalog(songs, Path(args.song_catalog), parsed_ok, args.dry_run)
        print(f"{'[dry-run] ' if args.dry_run else ''}song_catalog：{verb} {sc_changed} 首（notes/diff/dur/bpm；title/artist 保留）")
        for ex in sc_examples:
            print(f"    {ex}")

    if args.dry_run and (changed or (not args.no_song_catalog)):
        print("  → 確認無誤後拿掉 --dry-run 就地寫入。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
