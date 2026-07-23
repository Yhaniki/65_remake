# -*- coding: utf-8 -*-
"""
refresh_gn_header_stats — 就地刷新 song_table.csv 的「譜面數值」，但**保留歌名**。

用途:
  build_gn_header_catalog.py 是整份重掃（歌名以外的欄位全部重寫）。
  add_songs_incremental.py 只加新歌、完全不碰既有列。
  這支補中間那一塊：只針對「已經在表裡的歌」重讀 .gn 表頭，把數值欄位刷新成最新，
  歌名/歌手維持不動。不加歌、不刪歌，順序不變。

會刷新（重讀表頭覆蓋）:
  mode, chartBpm, lv*, notes*, meas*, dur*, producer, origName
會保留（人手改的、或別的工具管的）:
  title / artist  —— 顯示歌名，永遠沿用舊值
  bpm / offsetMs  —— 顯示 BPM 與音訊校正，只有人調得出來
  fileId          —— 可能是人工指定的「去撞號槽位」（重複歌 sdomNNNN_1k 借槽，如 1117_1=11138，
                     而表頭仍寫原曲 11117）；刷新會還原成表頭值造成撞號。絕不碰。
  enc/seed/innerOff/size —— 解密欄位平常不碰（那是 gn_keytable.py 的地盤），**加 --keys 才刷新**

來源與正確性:
  對每個 .gn **重新偵測**表頭位置/金鑰（gn_keytable.process_file），不信任表裡的舊 offset —
  檔案被換過(innerOff/seed 位移)也不會讀成亂碼。解不出金鑰、或表頭 fileType 不是 gn 的檔
  一律跳過、保留舊值，絕不把垃圾寫進表。music 目錄沒有的歌 → 原樣保留並記為 skipped。

用法:
  python tools/refresh_gn_header_stats.py --dry-run      # 只看會改幾首、改了什麼，不寫檔
  python tools/refresh_gn_header_stats.py                # 就地刷新
  python tools/refresh_gn_header_stats.py --music <dir> --table <song_table.csv>
  python tools/refresh_gn_header_stats.py --keys --only sdom2033k.gn,sdom5001k.gn
                                                        # 只對這幾個檔，連 seed 一起對回實際檔案
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path
from typing import Any, Dict, List

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from gn_keytable import STEPFILE_HEADER, process_file  # noqa: E402
from build_gn_header_catalog import header_bytes, parse_header, header_to_row  # noqa: E402
import song_table as st  # noqa: E402

# process_file 成功解碼的 enc（sdom_failed / None 代表沒解出，不可信）
GOOD_ENC = {"sdom", "ddrm", "plain", "rewu"}

REPO = HERE.parent

#: 重讀表頭後要比對/覆蓋的欄位（＝ header_to_row(names=False) 會寫到的那些）。
REFRESH_COLS = (
    "mode", "chartBpm",
    "lvEasy", "lvNormal", "lvHard",
    "notesEasy", "notesNormal", "notesHard",
    "measEasy", "measNormal", "measHard",
    "durEasy", "durNormal", "durHard",
    "producer", "origName",
)

#: 解密欄位。平常**不動**（那是 gn_keytable.py 的地盤），加 --keys 才一起刷新 —— 用在
#: 「同名檔案被換過一份」的時候：譜面內容一樣，但重新編碼/重新加密過，表裡存的 seed 就對不上了
#: （例：clean 樹的 sdom5001k 是 sdom 加密，表裡卻寫著 rewu）。runtime 靠 seed pool 硬試還是解得開，
#: 所以這比較像對帳而非救火，但表該講實話。
KEY_COLS = ("enc", "seed", "seed1", "seed2", "innerOff", "size")


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


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="就地刷新 song_table.csv 的譜面數值，保留歌名")
    ap.add_argument("--music", default=str(default_music()),
                    help="music 目錄(含 .gn)；預設讀 data_root.txt 的 DATA/MUSIC")
    ap.add_argument("--table", default=str(st.DEFAULT_CSV), help="song_table.csv(就地更新)")
    ap.add_argument("--keys", action="store_true",
                    help="連解密欄位(enc/seed/innerOff/size)也刷新 —— 同名檔案被換過一份時用")
    ap.add_argument("--only", default="",
                    help="只處理這幾個 .gn(逗號分隔，例 sdom2033k.gn,sdom5001k.gn)；預設全表")
    ap.add_argument("--dry-run", action="store_true", help="只報告不寫檔")
    args = ap.parse_args()

    only = {n.strip().lower() for n in args.only.split(",") if n.strip()}
    cols = REFRESH_COLS + (KEY_COLS if args.keys else ())

    music = Path(args.music)
    table_path = Path(args.table)
    if not music.is_dir():
        print(f"找不到 music 目錄: {music}", file=sys.stderr); return 1
    rows = st.load(table_path)
    if not rows:
        print(f"找不到/讀不到 {table_path}", file=sys.stderr); return 1

    seed_cache: Dict[bytes, int] = {}
    changed = 0
    changed_fields: Dict[str, int] = {}
    skipped: List[str] = []
    examples: List[str] = []

    for row in rows:
        gn = row["gn"]
        if only and gn not in only:
            continue
        fp = music / gn
        if not fp.is_file():
            skipped.append(f"{gn}(檔案不存在)"); continue
        try:
            raw = fp.read_bytes()
            song = process_file(raw, fp.name, seed_cache)  # 重新偵測 enc/offset/seed
            if song.get("enc") not in GOOD_ENC:
                skipped.append(f"{gn}(解不出:{song.get('enc')})"); continue
            h = header_bytes(raw, song)
            if h is None or len(h) < STEPFILE_HEADER:
                skipped.append(f"{gn}(表頭取不到)"); continue
            fresh = parse_header(h, {"gn": gn, "mode": song.get("mode") or row.get("mode") or ""})
            if fresh.get("fileType") not in ("gn", "GN"):
                skipped.append(f"{gn}(fileType={fresh.get('fileType')!r})"); continue
        except Exception as e:
            skipped.append(f"{gn}({e})"); continue

        # 先算到一份副本上，才知道「哪些欄位真的變了」；fileId 由 header_to_row 寫進副本後丟掉。
        after: Dict[str, Any] = dict(row)
        header_to_row(fresh, after, names=False)
        if args.keys:
            for c in KEY_COLS:
                after[c] = len(raw) if c == "size" else song.get(c)
        diffs = [c for c in cols if row.get(c) != after.get(c)]
        if not diffs:
            continue
        changed += 1
        for c in diffs:
            changed_fields[c] = changed_fields.get(c, 0) + 1
        if len(examples) < 6:
            examples.append(f"{gn}  " + ", ".join(f"{c}:{row.get(c)}→{after.get(c)}" for c in diffs))
        for c in diffs:
            row[c] = after[c]

    if not args.dry_run and changed:
        st.save(rows, table_path)

    verb = "會改" if args.dry_run else "已改"
    print(f"{'[dry-run] ' if args.dry_run else ''}掃 {len(rows)} 列；{verb} {changed} 列；"
          f"略過 {len(skipped)}（title/artist/bpm/offsetMs/fileId 全程保留）")
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
