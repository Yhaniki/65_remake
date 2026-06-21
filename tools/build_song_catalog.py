#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
build_song_catalog.py — 匯入時（import-time）把原版 SongList.dat 的 GB2312/GBK 文字
解碼成 UTF-8，輸出 song_catalog.json 給 runtime 用。

為什麼在這裡做：專案 runtime 是 Unity .NET Standard 2.1 + Android IL2CPP，BCL 不含
cp936（GB2312），裝置上無法可靠解 GB2312 → 直接亂碼。Python 不論 OS 語系都內建 gb18030
（GB2312 的超集），所以在 dev/CI 端解一次、存成 UTF-8，runtime 全程只碰 Unicode。

來源格式見 docs/reverse-engineering/SONGLIST_FORMAT.md。
輸出 UTF-8 **無 BOM**（Unity JsonUtility 不吃開頭 BOM）。

用法：
    python tools/build_song_catalog.py
    python tools/build_song_catalog.py <SongList.dat> <out.json> [--encoding gb18030]
"""
import argparse
import json
import os
import struct
import sys

# repo 內預設路徑（相對本檔位置）
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
DEFAULT_SRC = os.path.join(REPO, "assets", "sdox_offline", "music", "SongList.dat")
DEFAULT_OUT = os.path.join(REPO, "65", "My project", "Assets", "StreamingAssets", "song_catalog.json")


def decode(d, off, length, encoding):
    """讀 NUL 結尾字串並用 encoding 解碼。errors='replace' 讓壞 byte 變 U+FFFD 而非中斷。"""
    raw = d[off:off + length].split(b"\x00")[0]
    return raw.decode(encoding, "replace")


def parse_songlist(path, encoding):
    data = open(path, "rb").read()
    count = struct.unpack("<H", data[:2])[0]

    # header 大小偵測：找到 'sdom' 則 header 結束於該位置，否則最小 2 bytes
    sdom = data.find(b"sdom")
    header_size = sdom if sdom != -1 else 2

    rec_size = (len(data) - header_size) // count
    if rec_size not in (752, 756):
        raise SystemExit(f"unexpected rec_size={rec_size} (count={count}, header={header_size})")

    fo, no = (456, 564) if rec_size == 756 else (452, 560)

    songs = []
    for i in range(count):
        st = header_size + i * rec_size
        d = data[st:st + rec_size]
        gn = decode(d, 0, 32, encoding).strip()
        if not gn:
            continue
        rec = {
            "gn": gn.lower(),                       # 以小寫檔名當 key（runtime 不分大小寫查）
            "fileId": struct.unpack("<I", d[fo:fo + 4])[0],
            "title": decode(d, no, 64, encoding),
            "artist": decode(d, no + 64, 32, encoding),
        }
        # 難度等級三難度（兩種格式皆在 fo+20，見 SONGLIST_FORMAT.md 讀取範例）
        e, n, h = struct.unpack("<3H", d[fo + 20:fo + 26])
        rec["diffEasy"], rec["diffNormal"], rec["diffHard"] = e, n, h
        # bpm / note 數 / 長度(秒)只在 756（線上版完整表）內，752 單機版無此欄位。
        if rec_size == 756:
            rec["bpm"] = round(struct.unpack("<f", d[472:476])[0], 3)
            ne, nn, nh = struct.unpack("<3I", d[496:508])
            rec["notesEasy"], rec["notesNormal"], rec["notesHard"] = ne, nn, nh
            de, dn, dh = struct.unpack("<3I", d[728:740])
            # 防呆：壞值（負/超過一小時）視為未知(0)，避免 UI 顯示亂數長度。
            clamp = lambda s: s if 0 < s < 3600 else 0
            rec["durEasy"], rec["durNormal"], rec["durHard"] = clamp(de), clamp(dn), clamp(dh)
        songs.append(rec)
    return songs, rec_size, count


def main():
    ap = argparse.ArgumentParser(description="SongList.dat (GB2312) -> UTF-8 song_catalog.json")
    ap.add_argument("src", nargs="?", default=DEFAULT_SRC)
    ap.add_argument("out", nargs="?", default=DEFAULT_OUT)
    ap.add_argument("--encoding", default="gb18030",
                    help="原版文字編碼；大陸版 gb18030(含 GB2312/GBK)、台港版 big5")
    args = ap.parse_args()

    if not os.path.isfile(args.src):
        raise SystemExit(f"SongList.dat not found: {args.src}")

    songs, rec_size, count = parse_songlist(args.src, args.encoding)

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    # UTF-8 無 BOM；ensure_ascii=False 讓中文以原字寫出（仍是合法 UTF-8）
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"songs": songs}, f, ensure_ascii=False, indent=1)
        f.write("\n")

    sys.stdout.reconfigure(encoding="utf-8")
    print(f"[ok] {args.src}")
    print(f"     rec_size={rec_size} count={count} encoding={args.encoding}")
    print(f"     wrote {len(songs)} songs -> {args.out}")
    for s in songs[:3]:
        print(f"       {s['gn']}  {s['title']!r}  {s['artist']!r}")


if __name__ == "__main__":
    main()
