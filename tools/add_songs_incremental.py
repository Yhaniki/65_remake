# -*- coding: utf-8 -*-
"""
add_songs_incremental — 只把「指定的幾個 .gn」追加進 song_table.csv，不全量重掃。

為什麼要有這支:
  gn_keytable.py / build_gn_header_catalog.py / build_song_name_overrides.py
  都是「整個 music 目錄重掃、整份重寫」。加幾首新歌時全跑一遍會有兩個實害:
    1. build_song_name_overrides 加了 --songname 會拿原版 SONGNAME.TXT 無條件覆蓋那 575 首 →
       手改過的歌名被打回原樣(實際發生過: 周杰倫→周傑倫、七里香→七裏香、歌舞青春3 三首
       變回「A(10/24歌舞青春3:畢業季)」這種上架備註名)。現在 --songname 預設關掉，但整份重掃
       仍會動到一堆不該動的列。
    2. 重掃會把 music 目錄裡的實驗檔/殘骸(sdom2705k_edit.gn …)一起收進 catalog。
  再加上整份重排會產生上萬行 git diff。本工具只針對你點名的 .gn 做 upsert，其餘一個 byte 不動。

用法:
  python tools/add_songs_incremental.py assets/sdox_offline/music/sdom2950K.gn [更多 .gn ...]
  python tools/add_songs_incremental.py --music-dir assets/sdox_offline/music --stems sdom2950 sdom2951
  加 --dry-run 只看會動到什麼。

會更新(StreamingAssets):
  song_table.csv → 每個 .gn 一列(K 譜與 T 譜各一列)
既有列一律不覆寫(要改名直接編輯 song_table.csv 的 title / artist 欄)。
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path
from typing import Dict, List

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from gn_keytable import process_file  # noqa: E402
from build_gn_header_catalog import header_bytes, parse_header, header_to_row  # noqa: E402
from build_song_name_overrides import to_traditional  # noqa: E402
import song_table as stbl  # noqa: E402

REPO = HERE.parent
SA = stbl.SA
TABLE = stbl.DEFAULT_CSV


def add_gn_files(files: List[Path], force: bool = False, dry_run: bool = False,
                 file_id: int | None = None) -> List[str]:
    """把這些 .gn upsert 進 song_table.csv。回傳 log 行(呼叫端自己決定要不要印)。

    file_id: 指定 fileId，覆蓋 .gn 表頭裡那個。撞號插隊時用 —— 你把新歌的譜面改名成一個空號
    (sdom5086K.gn)，但 .gn 表頭內嵌的 fileId 仍是它老家的號，照抄會跟既有歌撞在一起
    (fileId 才是 DANCE/試聽/封面 的 key)。runtime 只讀表的 fileId、不讀 .gn 內嵌的那個，
    所以在這裡改寫就夠，不必動 .gn 位元組。None = 沿用表頭值(一般加歌路徑)。
    """
    rows = stbl.by_gn(stbl.load(TABLE))
    seed_cache: Dict[bytes, int] = {}
    log: List[str] = []
    touched = 0

    for p in sorted(files, key=lambda x: x.name.lower()):
        raw = p.read_bytes()
        e = process_file(raw, p.name, seed_cache)
        gn = p.name.lower()
        if e.get("enc") in (None, "unknown", "sdom_failed", "error"):
            log.append(f"!! {gn} 解不開(enc={e.get('enc')}) — 跳過")
            continue
        if gn in rows and not force:
            log.append(f"  skip(已存在，不覆寫手改) {gn}")
            continue

        hb = header_bytes(raw, {"gn": gn, **e})
        if hb is None:
            log.append(f"!! {gn} 取不到表頭 — 跳過")
            continue
        h_row = parse_header(hb, {"gn": gn, "mode": e.get("mode", "")})

        row = rows.get(gn) or stbl.blank_row(gn)
        for col in ("enc", "seed", "seed1", "seed2", "innerOff", "size"):
            row[col] = e.get(col)
        header_to_row(h_row, row)
        # 顯示名打底：表頭是簡中，轉繁後放進 title/artist（之後人要改就直接改這兩欄）。
        row["title"] = to_traditional((h_row.get("title") or {}).get("zhCN", ""))
        row["artist"] = to_traditional((h_row.get("artist") or {}).get("zhCN", ""))
        row["src"] = "kgn"
        row["bpm"] = row["chartBpm"]
        if file_id is not None:
            row["fileId"] = file_id
        log.append(("  覆寫 " if gn in rows else "  新增 ") + gn)
        rows[gn] = row
        touched += 1

    if dry_run:
        log.append("(dry-run，未寫檔)")
        return log

    if touched:
        stbl.save(rows.values(), TABLE)     # save 會照 gn 排序，並把顯示欄位同步到 K/T 兩列
    log.append(f"寫入完成 → song_table.csv 共 {len(rows)} 列(本次動到 {touched} 列)")
    return log


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="把指定的 .gn 增量追加進 song_table.csv(不全量重掃)")
    ap.add_argument("gn", nargs="*", help=".gn 檔案路徑")
    ap.add_argument("--music-dir", help="配合 --stems 使用")
    ap.add_argument("--stems", nargs="*", default=[], help="詞幹(sdom2950)，會找 K/T 兩譜")
    ap.add_argument("--force", action="store_true", help="已存在的 gn 也覆寫(預設跳過)")
    ap.add_argument("--file-id", type=int, help="指定 fileId(撞號插隊用；預設沿用 .gn 表頭)")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    files = [Path(g) for g in args.gn]
    if args.stems:
        md = Path(args.music_dir or (REPO / "assets" / "sdox_offline" / "music"))
        for stem in args.stems:
            files += [p for p in (md / f"{stem}K.gn", md / f"{stem}T.gn") if p.is_file()]
    files = [p for p in files if p.is_file()]
    if not files:
        print("沒有可處理的 .gn", file=sys.stderr)
        return 1

    for line in add_gn_files(files, force=args.force, dry_run=args.dry_run, file_id=args.file_id):
        print(line)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
