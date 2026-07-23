# -*- coding: utf-8 -*-
"""
build_gn_header_catalog — 把每個 .gn 的 StepFile 300-byte 表頭解出來，寫進 song_table.csv
（難度/音符數/長度/小節、簡繁英歌名、曲師、原始檔名）。

動機:
  .gn 表頭的文字(歌名/歌手/製作者/原檔名)是 GB2312/GBK(簡中)；Unity runtime
  (.NET Standard 2.1 / IL2CPP) 無 cp936 codec，裝置上解會亂碼且隨系統語系而變。
  本工具在 dev 端用 gb18030 解一次、寫成 UTF-8，runtime 全程只碰 Unicode，切換語系不會跑掉。

多語名:
  原始資料只有簡中。表裡分成三組欄位，方便 UI 切語言:
    titleZhCn / artistZhCn = 表頭原字(簡中或英文原名)
    title / artist         = 顯示名：以 opencc s2twp 由簡轉繁(台灣用字，含詞彙)。
                             **已經有值就不動** —— 那是人手改過的，重掃譜面不該蓋掉歌名。
    titleEn / artistEn     = 原字若為純 ASCII(本就英文/拉丁名)則填入，否則留空待補

表頭欄位(對齊 bms_sdo/gn_master_core.StepFileBin):
  fileId i32@0, fileType @4, bpm f32@16, levels 3*i16@20,
  noteCounts 3*i32@40, measurements 3*i32@64, durations 3*i32@272,
  title @108(32B), artist(=writer 欄) @172(32B), producer @204(32B), origName @236(32B)

來源:
  以 song_table.csv 列舉所有 .gn 並取得 enc/innerOff/seed（先跑 tools/gn_keytable.py）。
  取表頭不需暴力 seed: sdom 表頭在 raw[innerOff:+300] 即明文；ddrm 用檔頭 seed 秒解；plain 原樣。

用法:
  python tools/build_gn_header_catalog.py
  python tools/build_gn_header_catalog.py <music_dir> -o <song_table.csv>
"""
from __future__ import annotations

import argparse
import json
import os
import struct
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from gn_keytable import lcg_transform, decrypt_ddrm, STEPFILE_HEADER  # noqa: E402
import song_table as st  # noqa: E402

REPO = HERE.parent
DEFAULT_MUSIC = REPO / "assets" / "閉撰敃氪" / "music"
DEFAULT_OUT = st.DEFAULT_CSV

# 簡→繁(台灣，含詞彙) 轉換；缺套件時 zhTW 退回原字並提示
try:
    import opencc  # type: ignore
    _CC: Optional[Any] = opencc.OpenCC("s2twp")
except Exception:
    _CC = None


def decode_gb(raw: bytes) -> str:
    """NUL 截斷後以 gb18030(GB2312/GBK 超集)解碼；壞 byte → U+FFFD 不中斷。"""
    return raw.split(b"\x00", 1)[0].decode("gb18030", "replace").strip()


def localized(raw: bytes) -> Dict[str, str]:
    s = decode_gb(raw)
    is_ascii = bool(s) and all(ord(c) < 128 for c in s)
    return {
        "zhCN": s,
        "zhTW": _CC.convert(s) if (_CC and not is_ascii) else s,
        "en": s if is_ascii else "",
    }


def header_bytes(raw: bytes, song: Dict[str, Any]) -> Optional[bytes]:
    """取得該檔的 StepFile 300-byte 表頭(明文)。"""
    enc = song.get("enc")
    if enc == "sdom":
        off = int(song.get("innerOff", 0))
        return raw[off:off + STEPFILE_HEADER]
    if enc == "ddrm":
        dec = decrypt_ddrm(raw)
        return dec[0][:STEPFILE_HEADER] if dec else None
    if enc == "plain":
        return raw[:STEPFILE_HEADER]
    if enc == "rewu":
        # 熱舞 Online 整檔 LCG：從 offset 0 用存好的 seed 解前 300 bytes 即得明文表頭。
        seed = int(song.get("seed", 0))
        if not seed:
            return None
        return lcg_transform(seed, raw[:STEPFILE_HEADER], encrypt=False)
    return None


def parse_header(h: bytes, song: Dict[str, Any]) -> Dict[str, Any]:
    def i16x3(o): return list(struct.unpack_from("<hhh", h, o))
    def i32x3(o): return list(struct.unpack_from("<iii", h, o))
    bpm = struct.unpack_from("<f", h, 16)[0]
    return {
        "gn": song["gn"],
        "fileId": struct.unpack_from("<i", h, 0)[0],
        "fileType": h[4:8].split(b"\x00", 1)[0].decode("ascii", "replace"),
        "mode": song.get("mode", ""),
        "bpm": round(float(bpm), 4) if bpm == bpm and abs(bpm) < 1e6 else 0.0,
        "levels": i16x3(20),
        "noteCounts": i32x3(40),
        "measurements": i32x3(64),
        "durations": i32x3(272),
        "title": localized(h[108:140]),
        "artist": localized(h[172:204]),    # 表頭 writer 欄實為歌手(與 song_catalog 的 artist 一致)
        "producer": decode_gb(h[204:236]),  # 譜面製作者，多為 ASCII
        "origName": decode_gb(h[236:268]),  # 原始檔名
    }


def clamp_dur(sec: int) -> int:
    """壞值（負/超過一小時）視為未知(0)，避免 UI 顯示亂數長度。"""
    return sec if 0 < sec < 3600 else 0


def header_to_row(rec: Dict[str, Any], row: Dict[str, Any], names: bool = True) -> None:
    """parse_header 的結果 → song_table 的欄位（就地改 row）。

    names=False 只刷譜面數值，歌名一個字都不碰（tools/refresh_gn_header_stats.py 用）。
    就算 names=True，**已經有值的 title / artist 也不會被蓋掉** —— 那兩欄是人手改的，
    重掃譜面把校正過的歌名蓋回簡中原字，是這套工具以前最常犯的錯。"""
    row["fileId"] = rec["fileId"]
    row["mode"] = rec.get("mode") or row.get("mode") or ""
    row["chartBpm"] = rec["bpm"]
    for i, k in enumerate(("Easy", "Normal", "Hard")):
        row["lv" + k] = rec["levels"][i]
        row["notes" + k] = rec["noteCounts"][i]
        row["dur" + k] = clamp_dur(rec["durations"][i])
        row["meas" + k] = rec["measurements"][i]
    row["producer"] = rec["producer"]
    row["origName"] = rec["origName"]
    if not row.get("bpm") or row["bpm"] <= 0:
        row["bpm"] = rec["bpm"]
    if not names:
        return
    row["titleZhCn"], row["titleEn"] = rec["title"]["zhCN"], rec["title"]["en"]
    row["artistZhCn"], row["artistEn"] = rec["artist"]["zhCN"], rec["artist"]["en"]
    if not row.get("title"):
        row["title"] = rec["title"]["zhTW"] or rec["title"]["zhCN"]
    if not row.get("artist"):
        row["artist"] = rec["artist"]["zhTW"] or rec["artist"]["zhCN"]


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description=".gn 表頭 → song_table.csv 的譜面/歌名欄位")
    ap.add_argument("music", nargs="?", default=str(DEFAULT_MUSIC), help="music 目錄(含 .gn)")
    ap.add_argument("-o", "--output", default=str(DEFAULT_OUT), help="song_table.csv")
    args = ap.parse_args()

    music = Path(args.music)
    out_path = Path(args.output)
    if not music.is_dir():
        print(f"找不到 music 目錄: {music}", file=sys.stderr); return 1
    rows = st.by_gn(st.load(out_path))
    if not rows:
        print(f"{out_path} 是空的（先跑 tools/gn_keytable.py）", file=sys.stderr); return 1
    if _CC is None:
        print("[warn] 未安裝 opencc-python-reimplemented，zhTW 暫以簡中原字代替；"
              "pip install opencc-python-reimplemented 後重跑可填繁中。", file=sys.stderr)

    done: List[Dict[str, Any]] = []
    skipped: List[str] = []
    bad_decode = 0
    for gn, row in rows.items():
        fp = music / gn
        if not fp.is_file():
            skipped.append(gn); continue
        try:
            raw = fp.read_bytes()
            h = header_bytes(raw, {"enc": row.get("enc"), "innerOff": row.get("innerOff") or 0,
                                   "seed": row.get("seed") or 0})
            if h is None or len(h) < STEPFILE_HEADER:
                skipped.append(gn); continue
            rec = parse_header(h, {"gn": gn, "mode": row.get("mode") or ""})
            if "�" in rec["title"]["zhCN"] or "�" in rec["artist"]["zhCN"]:
                bad_decode += 1
            header_to_row(rec, row)
            done.append(rec)
        except Exception as e:
            skipped.append(f"{gn}({e})")

    st.save(rows.values(), out_path)
    print(f"完成：{out_path}")
    print(f"  寫入 {len(done)} 首；略過 {len(skipped)}；含 U+FFFD 解碼疑慮 {bad_decode}")
    if skipped[:5]:
        print(f"  略過樣本：{skipped[:5]}")
    for rec in done[:4]:
        print(f"    {rec['gn']:16s} title.zhCN={rec['title']['zhCN']!r} "
              f"zhTW={rec['title']['zhTW']!r} artist.zhCN={rec['artist']['zhCN']!r}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
