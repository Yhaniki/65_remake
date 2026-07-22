# -*- coding: utf-8 -*-
"""
build_song_table.py — 把舊的四份 JSON 併成單一 StreamingAssets/song_table.csv。

  gn_header_catalog.json   → 難度/音符數/長度/小節/簡繁英歌名/曲師/原始檔名
  gn_keytable.json         → enc/seed/seed1/seed2/innerOff/size
  song_catalog.json        → （完全由表頭衍生，只拿來對帳，不提供新資料）
  song_name_overrides.json → 手改的 title/artist/bpm/offsetMs/src（覆蓋到該詞幹的 k/t 兩列）

這是**一次性搬家**用的（之後所有工具直接讀寫 song_table.csv）。重跑是冪等的：
預設會保留現有 song_table.csv 裡「JSON 沒有的欄位值」，只把四份 JSON 的內容蓋上去。

用法:
  python tools/build_song_table.py                 # 產生/更新 song_table.csv
  python tools/build_song_table.py --check         # 只比對，不寫檔（跑完印差異）
"""
from __future__ import annotations

import argparse
import io
import json
import sys
from pathlib import Path
from typing import Dict

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import song_table as st  # noqa: E402

SA = st.SA
HEADERS = SA / "gn_header_catalog.json"
KEYTABLE = SA / "gn_keytable.json"
CATALOG = SA / "song_catalog.json"
OVERRIDES = SA / "song_name_overrides.json"


def _songs(path: Path) -> list:
    if not path.is_file():
        print(f"  ! {path.name} 不存在，略過")
        return []
    return json.loads(io.open(path, "r", encoding="utf-8").read()).get("songs", [])


def _at(seq, i):
    try:
        return seq[i]
    except (IndexError, TypeError):
        return None


def build() -> Dict[str, Dict]:
    rows: Dict[str, Dict] = {r["gn"]: r for r in st.load()}      # 既有值打底（冪等重跑）

    def row(gn: str) -> Dict:
        gn = gn.strip().lower()
        return rows.setdefault(gn, st.blank_row(gn))

    # 1) 表頭：譜面數值 + 原文歌名 + 曲師
    heads = _songs(HEADERS)
    for h in heads:
        r = row(h["gn"])
        r["fileId"] = h.get("fileId")
        r["mode"] = h.get("mode") or ""
        r["chartBpm"] = h.get("bpm")
        for i, key in enumerate(("Easy", "Normal", "Hard")):
            r["lv" + key] = _at(h.get("levels"), i)
            r["notes" + key] = _at(h.get("noteCounts"), i)
            r["dur" + key] = _at(h.get("durations"), i)
            r["meas" + key] = _at(h.get("measurements"), i)
        title, artist = h.get("title") or {}, h.get("artist") or {}
        r["titleZhCn"], r["titleEn"] = title.get("zhCN", ""), title.get("en", "")
        r["artistZhCn"], r["artistEn"] = artist.get("zhCN", ""), artist.get("en", "")
        r["producer"] = h.get("producer") or ""
        r["origName"] = h.get("origName") or ""
        # 打底的顯示值：先用表頭的繁體，之後被 overrides 蓋掉
        r["title"] = title.get("zhTW") or title.get("zhCN", "")
        r["artist"] = artist.get("zhTW") or artist.get("zhCN", "")
        r["bpm"] = h.get("bpm")

    # 2) 金鑰表：解密資訊（含表頭沒有的空檔 sdom1096k/t）
    keys = _songs(KEYTABLE)
    for k in keys:
        r = row(k["gn"])
        r["enc"] = k.get("enc") or ""
        for c in ("seed", "seed1", "seed2", "innerOff", "size"):
            if k.get(c) is not None:
                r[c] = k[c]
        if r.get("fileId") is None:
            r["fileId"] = k.get("fileId")
        if not r.get("mode"):
            r["mode"] = k.get("mode") or ""
        if r.get("chartBpm") is None:
            r["chartBpm"] = k.get("bpm")

    # 3) 手改清單：詞幹 → 蓋到 k/t 兩列
    overs = _songs(OVERRIDES)
    by_stem = {st.stem(o["gn"]): o for o in overs if o.get("gn")}
    hit = 0
    for r in rows.values():
        o = by_stem.get(st.stem(r["gn"]))
        if not o:
            continue
        hit += 1
        if o.get("title"):
            r["title"] = o["title"]
        if o.get("artist"):
            r["artist"] = o["artist"]
        if (o.get("bpm") or -1) > 0:
            r["bpm"] = o["bpm"]
        r["offsetMs"] = o.get("offsetMs") or 0.0
        r["src"] = o.get("src") or ""

    print(f"  表頭 {len(heads)} 筆 / 金鑰 {len(keys)} 筆 / 手改 {len(overs)} 筆（套到 {hit} 列）")

    # 4) 對帳：song_catalog.json 應該完全是表頭的衍生物
    bad = []
    for c in _songs(CATALOG):
        r = rows.get((c.get("gn") or "").lower())
        if r is None:
            bad.append(f"{c.get('gn')}: 表頭沒有這首")
            continue
        for jk, ck in (("diffEasy", "lvEasy"), ("diffNormal", "lvNormal"), ("diffHard", "lvHard"),
                       ("notesEasy", "notesEasy"), ("notesNormal", "notesNormal"), ("notesHard", "notesHard"),
                       ("durEasy", "durEasy"), ("durNormal", "durNormal"), ("durHard", "durHard"),
                       ("fileId", "fileId")):
            if c.get(jk) != r.get(ck):
                bad.append(f"{c.get('gn')}: {jk} {c.get(jk)} != {ck} {r.get(ck)}")
    if bad:
        print(f"  ! song_catalog.json 與表頭有 {len(bad)} 處不一致（以表頭為準）：")
        for line in bad[:10]:
            print("    " + line)
    else:
        print("  song_catalog.json 與表頭完全一致 ✓")

    return rows


def main() -> int:
    ap = argparse.ArgumentParser(description="四份 JSON → 單一 song_table.csv")
    ap.add_argument("--check", action="store_true", help="只比對不寫檔")
    ap.add_argument("-o", "--out", type=Path, default=st.DEFAULT_CSV)
    args = ap.parse_args()

    rows = build()
    if args.check:
        print(f"  （--check）不寫檔；共 {len(rows)} 列")
        return 0
    n = st.save(rows.values(), args.out)
    print(f"  → {args.out}（{n} 列，{args.out.stat().st_size:,} bytes）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
