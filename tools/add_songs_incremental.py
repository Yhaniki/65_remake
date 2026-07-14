# -*- coding: utf-8 -*-
"""
add_songs_incremental — 只把「指定的幾個 .gn」追加進四張表，不全量重掃。

為什麼要有這支:
  gn_keytable.py / build_gn_header_catalog.py / build_song_catalog_from_gn.py /
  build_song_name_overrides.py 都是「整個 music 目錄重掃、整份重寫」。加幾首新歌時全跑一遍
  會有兩個實害:
    1. build_song_name_overrides 重跑會拿 SONGNAME.TXT 覆蓋 src=songname 的曲 →
       手改過的歌名被打回原樣(實際發生過: 周杰倫→周傑倫、七里香→七裏香、歌舞青春3 三首
       變回「A(10/24歌舞青春3:畢業季)」這種上架備註名)。
    2. 重掃會把 music 目錄裡的實驗檔/殘骸(sdom2705k_edit.gn …)一起收進 catalog。
  再加上整份重排會產生上萬行 git diff。本工具只針對你點名的 .gn 做 upsert，其餘一個 byte 不動。

用法:
  python tools/add_songs_incremental.py assets/sdox_offline/music/sdom2950K.gn [更多 .gn ...]
  python tools/add_songs_incremental.py --music-dir assets/sdox_offline/music --stems sdom2950 sdom2951
  加 --dry-run 只看會動到什麼。

會更新(StreamingAssets):
  gn_keytable.json / gn_header_catalog.json / song_catalog.json  → 每個 .gn 一筆
  song_name_overrides.json(+.csv)                                → 每首歌(K 譜)一筆，僅在缺該筆時新增
既有筆一律不覆寫(要改名請直接編輯 song_name_overrides.csv 再 song_names_csv.py apply)。
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Dict, List

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from gn_keytable import process_file  # noqa: E402
from build_gn_header_catalog import header_bytes, parse_header  # noqa: E402
from build_song_catalog_from_gn import clamp_dur  # noqa: E402
from build_song_name_overrides import to_traditional  # noqa: E402

REPO = HERE.parent
SA = REPO / "65" / "My project" / "Assets" / "StreamingAssets"
KEYTABLE = SA / "gn_keytable.json"
HEADERS = SA / "gn_header_catalog.json"
CATALOG = SA / "song_catalog.json"
OVERRIDES = SA / "song_name_overrides.json"

KEY_FIELDS = ("enc", "mode", "seed", "innerOff", "seed1", "seed2",
              "fileId", "bpm", "title", "size", "error")


def load(p: Path):
    raw = p.read_bytes().decode("utf-8")
    return json.loads(raw), ("\r\n" in raw)


def save(p: Path, obj: Any, crlf: bool) -> None:
    txt = json.dumps(obj, ensure_ascii=False, indent=1)
    if crlf:
        txt = txt.replace("\n", "\r\n")
    p.write_bytes(txt.encode("utf-8"))


def stem_of(gn: str) -> str:
    """sdom2950k.gn → sdom2950（K/T 兩譜共用一個詞幹）。"""
    s = gn[:-3] if gn.lower().endswith(".gn") else gn
    return s[:-1] if s[-1:] in "kKtT" else s


def catalog_row(h: Dict[str, Any], lang: str = "zhCN") -> Dict[str, Any]:
    levels = (h.get("levels") or [0, 0, 0]) + [0, 0, 0]
    notes = (h.get("noteCounts") or [0, 0, 0]) + [0, 0, 0]
    durs = (h.get("durations") or [0, 0, 0]) + [0, 0, 0]
    title = (h.get("title") or {}).get(lang) or (h.get("title") or {}).get("zhCN", "")
    artist = (h.get("artist") or {}).get(lang) or (h.get("artist") or {}).get("zhCN", "")
    return {
        "gn": h["gn"].lower(),
        "fileId": int(h.get("fileId", 0)),
        "title": title,
        "artist": artist,
        "bpm": round(float(h.get("bpm", 0.0)), 3),
        "diffEasy": int(levels[0]), "diffNormal": int(levels[1]), "diffHard": int(levels[2]),
        "notesEasy": int(notes[0]), "notesNormal": int(notes[1]), "notesHard": int(notes[2]),
        "durEasy": clamp_dur(int(durs[0])), "durNormal": clamp_dur(int(durs[1])), "durHard": clamp_dur(int(durs[2])),
    }


def upsert(songs: List[Dict[str, Any]], row: Dict[str, Any], sort_key, label: str,
           force: bool) -> str:
    idx = next((i for i, s in enumerate(songs) if str(s.get("gn", "")).lower() == row["gn"].lower()), None)
    if idx is not None:
        if not force:
            return f"skip(已存在) {label}"
        songs[idx] = row
        return f"覆寫 {label}"
    songs.append(row)
    songs.sort(key=sort_key)
    return f"新增 {label}"


def add_gn_files(files: List[Path], force: bool = False, dry_run: bool = False,
                 file_id: int | None = None) -> List[str]:
    """把這些 .gn upsert 進四張表。回傳 log 行(呼叫端自己決定要不要印)。

    file_id: 指定 fileId，覆蓋 .gn 表頭裡那個。撞號插隊時用 —— 你把新歌的譜面改名成一個空號
    (sdom5086K.gn)，但 .gn 表頭內嵌的 fileId 仍是它老家的號，照抄會跟既有歌撞在一起
    (fileId 才是 DANCE/試聽/封面 的 key)。runtime 只讀目錄表的 fileId、不讀 .gn 內嵌的那個，
    所以在這裡改寫就夠，不必動 .gn 位元組。None = 沿用表頭值(一般加歌路徑)。
    """
    kt, kt_crlf = load(KEYTABLE)
    hc, hc_crlf = load(HEADERS)
    sc, sc_crlf = load(CATALOG)
    ov, ov_crlf = load(OVERRIDES)

    by_name = lambda s: s["gn"].lower()                       # noqa: E731  keytable/header = 檔名序
    by_fid = lambda s: (s["fileId"], s["gn"])                 # noqa: E731  song_catalog = fileId 序
    seed_cache: Dict[bytes, int] = {}
    log: List[str] = []

    for p in sorted(files, key=lambda x: x.name.lower()):
        raw = p.read_bytes()
        e = process_file(raw, p.name, seed_cache)
        gn = p.name.lower()
        if e.get("enc") in (None, "unknown", "sdom_failed", "error"):
            log.append(f"!! {gn} 解不開(enc={e.get('enc')}) — 跳過")
            continue

        kt_row = {"gn": gn, **{k: e[k] for k in KEY_FIELDS if k in e}}
        if file_id is not None:
            kt_row["fileId"] = file_id
        log.append("  keytable  " + upsert(kt["songs"], kt_row, by_name, gn, force))

        hb = header_bytes(raw, {"gn": gn, **e})
        if hb is None:
            log.append(f"!! {gn} 取不到表頭 — 跳過")
            continue
        h_row = parse_header(hb, {"gn": gn, "mode": e.get("mode", "")})
        if file_id is not None:
            h_row["fileId"] = file_id
        log.append("  header    " + upsert(hc["songs"], h_row, by_name, gn, force))
        log.append("  catalog   " + upsert(sc["songs"], catalog_row(h_row), by_fid, gn, force))

        if e.get("mode", "").upper() != "K":          # overrides 一首歌一筆，掛在 K 譜
            continue
        stem = stem_of(gn)
        if any(s.get("gn") == stem for s in ov["songs"]):
            log.append(f"  overrides skip(已存在，不覆寫手改) {stem}")
            continue
        ov["songs"].append({                          # 追加在尾端 → 不動既有順序
            "gn": stem,
            "fileId": int(h_row.get("fileId", 0)),
            "bpm": round(float(h_row.get("bpm", 0.0)), 3),
            "src": "kgn",
            "title": to_traditional((h_row.get("title") or {}).get("zhCN", "")),
            "artist": to_traditional((h_row.get("artist") or {}).get("zhCN", "")),
        })
        log.append(f"  overrides 新增 {stem}")

    if dry_run:
        log.append("(dry-run，未寫檔)")
        return log

    if isinstance(kt.get("counts"), dict) and "total" in kt["counts"]:
        kt["counts"]["total"] = len(kt["songs"])
    if "count" in ov:
        ov["count"] = len(ov["songs"])

    save(KEYTABLE, kt, kt_crlf)
    save(HEADERS, hc, hc_crlf)
    save(CATALOG, sc, sc_crlf)
    save(OVERRIDES, ov, ov_crlf)
    log.append(f"寫入完成 → keytable {len(kt['songs'])} / header {len(hc['songs'])} / "
               f"catalog {len(sc['songs'])} / overrides {len(ov['songs'])}")
    return log


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="把指定的 .gn 增量追加進四張表(不全量重掃)")
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
        for st in args.stems:
            files += [p for p in (md / f"{st}K.gn", md / f"{st}T.gn") if p.is_file()]
    files = [p for p in files if p.is_file()]
    if not files:
        print("沒有可處理的 .gn", file=sys.stderr)
        return 1

    for line in add_gn_files(files, force=args.force, dry_run=args.dry_run, file_id=args.file_id):
        print(line)
    if not args.dry_run:
        print("CSV 鏡像：python tools/song_names_csv.py export")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
