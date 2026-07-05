#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
build_song_catalog_from_gn.py — 由「實際存在的 .gn 譜面」建 song_catalog.json，而非 SongList.dat。

為什麼:
  官方 SongList.dat 是「全曲目主表」（含未開放/未下載的空檔曲），且未必涵蓋資料夾內
  額外存在的譜面。遊戲歌單要顯示的是「這台機器真的有譜面可以玩的歌」= music 目錄裡
  非空的 .gn。gn_header_catalog.json 正好只列出「非空且可解密」的 .gn（sdom/ddrm/plain/
  rewu 皆已解出表頭），因此拿它當歌單成員來源最準：有 k.gn 就進歌單，空檔曲自動排除。

  文字沿用既有慣例走 zhCN（簡中原字）；要繁中顯示可改讀 gn_header_catalog 的 zhTW 欄
  或走 GnHeaderCatalog.cs（不影響本檔）。輸出 schema/風格與 build_song_catalog.py 一致，
  runtime SongCatalog.cs 直接讀。

用法:
  python tools/build_song_catalog_from_gn.py
  python tools/build_song_catalog_from_gn.py <gn_header_catalog.json> <out song_catalog.json> [--lang zhCN|zhTW]
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
SA = os.path.join(REPO, "65", "My project", "Assets", "StreamingAssets")
DEFAULT_SRC = os.path.join(SA, "gn_header_catalog.json")
DEFAULT_OUT = os.path.join(SA, "song_catalog.json")


def clamp_dur(s):
    """壞值（負/超過一小時）視為未知(0)，避免 UI 顯示亂數長度（對齊 build_song_catalog.py）。"""
    return s if 0 < s < 3600 else 0


def build(src, lang):
    hdr = json.load(open(src, encoding="utf-8"))
    songs = []
    for s in hdr.get("songs", []):
        gn = s.get("gn")
        if not gn:
            continue
        levels = (s.get("levels") or [0, 0, 0]) + [0, 0, 0]
        notes = (s.get("noteCounts") or [0, 0, 0]) + [0, 0, 0]
        durs = (s.get("durations") or [0, 0, 0]) + [0, 0, 0]
        title = (s.get("title") or {}).get(lang) or (s.get("title") or {}).get("zhCN", "")
        artist = (s.get("artist") or {}).get(lang) or (s.get("artist") or {}).get("zhCN", "")
        songs.append({
            "gn": gn.lower(),
            "fileId": int(s.get("fileId", 0)),
            "title": title,
            "artist": artist,
            "bpm": round(float(s.get("bpm", 0.0)), 3),
            "diffEasy": int(levels[0]), "diffNormal": int(levels[1]), "diffHard": int(levels[2]),
            "notesEasy": int(notes[0]), "notesNormal": int(notes[1]), "notesHard": int(notes[2]),
            "durEasy": clamp_dur(int(durs[0])), "durNormal": clamp_dur(int(durs[1])), "durHard": clamp_dur(int(durs[2])),
        })
    # 依 fileId 排序，跨次執行可重現（runtime SongListModel.Curate 仍會自行排序）
    songs.sort(key=lambda x: (x["fileId"], x["gn"]))
    return songs


def main():
    ap = argparse.ArgumentParser(description="gn_header_catalog.json -> song_catalog.json（成員=實際存在的 .gn）")
    ap.add_argument("src", nargs="?", default=DEFAULT_SRC)
    ap.add_argument("out", nargs="?", default=DEFAULT_OUT)
    ap.add_argument("--lang", default="zhCN", choices=["zhCN", "zhTW", "en"],
                    help="title/artist 取用語言（預設 zhCN 簡中，沿用既有慣例）")
    args = ap.parse_args()

    if not os.path.isfile(args.src):
        raise SystemExit(f"gn_header_catalog.json not found: {args.src}（先跑 build_gn_header_catalog.py）")

    songs = build(args.src, args.lang)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"songs": songs}, f, ensure_ascii=False, indent=1)
        f.write("\n")

    sys.stdout.reconfigure(encoding="utf-8")
    k = sum(1 for s in songs if s["gn"][:-3].endswith("k"))
    print(f"[ok] {args.src}")
    print(f"     wrote {len(songs)} rows ({k} 'k' songs shown) -> {args.out}  lang={args.lang}")
    for s in songs[-3:]:
        print(f"       {s['gn']}  {s['title']!r}  {s['artist']!r}  bpm={s['bpm']}  diff={s['diffEasy']}/{s['diffNormal']}/{s['diffHard']}")


if __name__ == "__main__":
    main()
