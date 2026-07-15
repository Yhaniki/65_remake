# -*- coding: utf-8 -*-
"""
build_song_name_overrides.py — 產生「一份涵蓋全部歌曲、可手動編輯的歌名清單」。

為什麼:
  遊戲歌名原本直接取自各 .gn 內嵌 StepFile 表頭(k.gn)，但有些 .gn 表頭的歌名/歌手是錯的
  (檔位被回收、metadata 沒更新)。改成讓遊戲讀這份清單顯示歌名，方便你隨時就地改名。

  清單涵蓋 song_catalog.json 裡的每一首歌(以 gn 詞幹 sdomNNNN 為 key，同首 K/T 譜共用一筆)，
  每首 title/artist 預先填好:
    - 開放曲(在官方 songlist.dat 且列於 open_list.txt)      -> 用 songlist.dat 的權威歌名
    - 其餘(未開放 / songlist 未收錄 / 教學曲…)               -> 用該曲 k.gn 的名字當預設
  未開放曲「不採用」songlist.dat 的名字(那些檔位被回收、資訊不對)，改用 k.gn 的名字打底。
  之後任何一首名字要改，直接編輯這份 json 的那一行即可。

  runtime SongCatalog 會把這裡的 title/artist 覆蓋到 k.gn 名字上(見 SongCatalog.ApplyNameOverrides)。
  只覆蓋顯示名(title/artist)；bpm/難度/音符數仍走實際譜面。fileId/bpm 只是給人辨識用的參考欄。

重建保護(重要):
  預設為「merge-preserve」— 重跑時**保留你已手改的每一筆**，只補上新出現的歌。所以之後新增
  歌曲後可安心重跑。要「整份由來源重新填、丟棄手改」才需要 --reseed。

編碼/繁體:
  songlist.dat 內嵌文字是 GB2312/GBK(簡中)；本工具 dev 端用 gb18030 解一次寫成 UTF-8(無 BOM)。
  歌名再以 opencc s2twp 由簡轉繁(台灣用字)；英文、日文(含假名)、已是繁體的字串一律原樣不動
  (避免 范→範、群→羣 這類對已繁體文字的誤轉)。需 `pip install opencc-python-reimplemented`；
  缺套件時原樣輸出(簡中)並提示。

用法:
  python tools/build_song_name_overrides.py            # 產生 or merge-preserve(保留手改, 補新歌)
  python tools/build_song_name_overrides.py --reseed   # 整份由 songlist.dat + k.gn 重填(丟棄手改!)
"""
from __future__ import annotations

import argparse
import json
import re
import struct
import sys
from pathlib import Path
from typing import Dict, List, Tuple

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
SA = REPO / "65" / "My project" / "Assets" / "StreamingAssets"
DEFAULT_CATALOG = SA / "song_catalog.json"
DEFAULT_SONGLIST = Path(r"H:\sdo_tw\15熱舞 Online(金富貴寶寶)\Music\songlist.dat")
DEFAULT_OPENLIST = Path(r"H:\sdo_tw\15熱舞 Online(金富貴寶寶)\Music\open_list.txt")
DEFAULT_OUT = SA / "song_name_overrides.json"

# songlist.dat 版面(逆向確認): u16 count @0, 之後每筆 756 bytes。內嵌 300-byte StepFile 表頭 @ +456。
#   gn 檔名 @+0(32B)；表頭欄位(對齊 build_gn_header_catalog.py): fileId@0, bpm f32@16,
#   title@108(32B), artist(=writer)@172(32B)
REC_SIZE = 756
HDR_OFF = 456

# SONGNAME.TXT = 遊戲實際顯示用的權威歌名表(逗號分隔 `id,flag,歌名,歌手`，Big5/big5hkscs 編碼、
# 原生繁體)。id = 曲號(sdomNNNN 的 NNNN)。這是第一順位來源：涵蓋到的曲一律以它為準(歌名+歌手)，
# 且是原生繁體不需 opencc。之前佔位/亂碼(975/9you/血淚remix…)的真名都在這裡。
DEFAULT_SONGNAME = Path(r"H:\sdo\熱舞 Online(金富貴寶寶)\DATA\SONGNAME.TXT")

# 人工修正表(僅供 SONGNAME 未涵蓋的曲)。目前 SONGNAME 已涵蓋所有已知需修正的曲，故留空。
KNOWN_CORRECTIONS: Dict[str, Tuple[str, str]] = {}


def parse_songname(path: Path) -> Dict[str, Tuple[str, str]]:
    """讀 SONGNAME.TXT → {gn 詞幹: (歌名, 歌手)}。Big5 有少數字(如「裏」)要 big5hkscs 才解得出。"""
    if not path.is_file():
        return {}
    raw = path.read_bytes()
    out: Dict[str, Tuple[str, str]] = {}
    for ln in raw.replace(b"\r\n", b"\n").split(b"\n"):
        f = ln.split(b",")
        if len(f) < 4 or not f[0].isdigit():
            continue
        sid = int(f[0])
        name = f[2].decode("big5hkscs", "replace").strip()
        artist = f[3].decode("big5hkscs", "replace").strip()
        out["sdom%04d" % sid] = (name, artist)   # 以詞幹為 key，避免與教學曲 sdom1 等撞號
    return out

# 簡→繁(台灣，含詞彙) 轉換。缺套件時原樣保留並提示。
try:
    import opencc  # type: ignore
    _S2TWP = opencc.OpenCC("s2twp")
    _T2S = opencc.OpenCC("t2s")
except Exception:
    _S2TWP = _T2S = None


def _kana(c: str) -> bool:
    o = ord(c)
    return (0x3040 <= o <= 0x30ff) or (0x31f0 <= o <= 0x31ff) or (0xff66 <= o <= 0xff9f)


def to_traditional(s: str) -> str:
    """把「純簡體中文」字串轉繁體(台灣用字)；英文、日文(含假名)、已含繁體字者一律原樣。

    只轉「含漢字、無假名、且不含繁體專用字」的字串 — 因為 opencc 對已是繁體的文字會誤轉
    (范→範、群→羣…多對一歧義)，故凡含繁體專用字(t2s 會改變的字)即視為已繁體而跳過。"""
    if not _S2TWP or not s:
        return s
    han = [c for c in s if 0x4e00 <= ord(c) <= 0x9fff]
    if not han:                                   # 無漢字(英文/符號) -> 不動
        return s
    if any(_kana(c) for c in s):                  # 含假名(日文) -> 不動
        return s
    if any(_T2S.convert(c) != c for c in han):    # 含繁體專用字(已是繁體/混合) -> 不動
        return s
    return _S2TWP.convert(s)


def cstr(b: bytes) -> bytes:
    return b.split(b"\x00", 1)[0]


def decode_gb(b: bytes) -> str:
    return cstr(b).decode("gb18030", "replace").strip()


def stem(gn: str) -> str:
    """sdom0001K.gn / sdom0001k.gn / sdom0001t.gn -> sdom0001（同首 K/T 共用一筆）。"""
    g = (gn or "").lower()
    g = g.rsplit("/", 1)[-1].rsplit("\\", 1)[-1]
    if g.endswith(".gn"):
        g = g[:-3]
    if g and g[-1] in ("k", "t"):
        g = g[:-1]
    return g


def song_number(gn: str) -> int:
    m = re.match(r"sdom0*([0-9]+)", (gn or "").lower())
    return int(m.group(1)) if m else -1


def parse_songlist_open(songlist: Path, openlist: Path) -> Dict[str, Dict]:
    """回傳 {stem: {title, artist, fileId, bpm}}，只含 open_list.txt 列出的開放曲。"""
    raw = songlist.read_bytes()
    n = struct.unpack_from("<H", raw, 0)[0]
    open_ids = set(int(x) for x in openlist.read_text().split())
    out: Dict[str, Dict] = {}
    for i in range(n):
        o = 2 + i * REC_SIZE
        gn = cstr(raw[o:o + 32]).decode("ascii", "replace")
        if song_number(gn) not in open_ids:
            continue                       # 未開放 -> 不採用其名(資訊不對)
        h = o + HDR_OFF
        fid = struct.unpack_from("<i", raw, h + 0)[0]
        bpm = struct.unpack_from("<f", raw, h + 16)[0]
        title = decode_gb(raw[h + 108:h + 140])
        artist = decode_gb(raw[h + 172:h + 204])
        s = stem(gn)
        if s not in out:                   # 同首 K/T 取先出現(K)
            out[s] = {"title": title, "artist": artist, "fileId": int(fid),
                      "bpm": round(float(bpm), 3) if bpm == bpm and abs(bpm) < 1e6 else 0.0}
    return out


def catalog_by_stem(catalog: Path) -> Tuple[List[str], Dict[str, Dict]]:
    """讀 song_catalog.json，回傳 (依 fileId 由大到小排序的 stem 清單, {stem: k.gn 名字/欄位})。
    每個 stem 取其 K 譜條目(顯示用的主譜)當 k.gn 打底，缺則取任一。"""
    songs = json.loads(catalog.read_text(encoding="utf-8")).get("songs", [])
    by: Dict[str, Dict] = {}
    for e in songs:
        gn = e.get("gn") or ""
        s = stem(gn)
        if not s:
            continue
        is_k = gn.lower().endswith("k.gn") or not gn.lower().rstrip(".gn").endswith("t")
        cur = by.get(s)
        rec = {"title": e.get("title", ""), "artist": e.get("artist", ""),
               "fileId": int(e.get("fileId", 0)), "bpm": round(float(e.get("bpm", 0.0)), 3),
               "_is_k": gn.lower().endswith("k.gn")}
        if cur is None or (rec["_is_k"] and not cur.get("_is_k")):
            by[s] = rec
    for r in by.values():
        r.pop("_is_k", None)
    order = sorted(by.keys(), key=lambda s: (-by[s]["fileId"], s))
    return order, by


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="全曲可編輯歌名清單 song_name_overrides.json")
    ap.add_argument("catalog", nargs="?", default=str(DEFAULT_CATALOG))
    ap.add_argument("songlist", nargs="?", default=str(DEFAULT_SONGLIST))
    ap.add_argument("openlist", nargs="?", default=str(DEFAULT_OPENLIST))
    ap.add_argument("-o", "--output", default=str(DEFAULT_OUT))
    ap.add_argument("--songname", default=str(DEFAULT_SONGNAME), help="SONGNAME.TXT(權威歌名表)路徑")
    ap.add_argument("--reseed", action="store_true",
                    help="整份由來源重填，丟棄既有手改(預設會保留 SONGNAME 未涵蓋曲的手改)")
    args = ap.parse_args()

    catalog = Path(args.catalog)
    songlist = Path(args.songlist)
    openlist = Path(args.openlist)
    out = Path(args.output)
    if not catalog.is_file():
        print(f"找不到 song_catalog.json: {catalog}（先跑 build_song_catalog_from_gn.py）", file=sys.stderr); return 1

    if _S2TWP is None:
        print("[warn] 未安裝 opencc-python-reimplemented，歌名暫以簡中原字輸出；"
              "pip install opencc-python-reimplemented 後重跑可轉繁體。", file=sys.stderr)

    songname = parse_songname(Path(args.songname))
    if songname:
        print(f"[ok] SONGNAME.TXT 權威歌名表：{len(songname)} 首（第一順位，原生繁體）")
    else:
        print(f"[warn] 讀不到 SONGNAME.TXT（{args.songname}），改用 songlist/k.gn。", file=sys.stderr)

    official = parse_songlist_open(songlist, openlist) if (songlist.is_file() and openlist.is_file()) else {}
    if not official:
        print(f"[warn] 讀不到 songlist.dat/open_list.txt（{songlist}），全部用 k.gn 名字打底。", file=sys.stderr)

    order, kgn = catalog_by_stem(catalog)

    # 舊檔：merge-preserve 用。**一律讀進來**（就算 --reseed 也要）——
    # --reseed 的語意是「歌名整份重填」，不該把手調的 offsetMs 一起炸掉：那是人耳一首一首對出來的，
    # 沒有任何地方能還原。歌名的保留才受 --reseed 控制（下面 keep_names）。
    prev: Dict[str, Dict] = {}
    if out.exists():
        try:
            for r in json.loads(out.read_text(encoding="utf-8")).get("songs", []):
                if r.get("gn"):
                    prev[stem(r["gn"])] = r
        except Exception as e:
            print(f"[warn] 舊清單解析失敗，改為全新產生：{e}", file=sys.stderr)
    keep_names = not args.reseed

    rows: List[Dict] = []
    n_songname = n_official = n_kgn = n_kept = n_new = n_fixed = 0
    for s in order:
        base = kgn[s]
        off = official.get(s)
        sn = songname.get(s)
        if sn:
            # SONGNAME.TXT 權威：原生繁體、歌名+歌手都以它為準，覆蓋 songlist/k.gn/手改，不套 opencc
            title, artist, src = sn[0], sn[1], "songname"
            n_songname += 1
        else:
            # SONGNAME 未涵蓋：開放曲用官方 songlist、其餘用 k.gn；純簡體轉繁(英文/日文/已繁不動)
            if off:
                title, artist, src = to_traditional(off["title"]), to_traditional(off["artist"]), "official"
                n_official += 1
            else:
                title, artist, src = to_traditional(base["title"]), to_traditional(base["artist"]), "kgn"
                n_kgn += 1
            fix = KNOWN_CORRECTIONS.get(s)
            if fix:
                title, artist, src = fix[0], fix[1], "fixed"
                n_fixed += 1
            # merge-preserve：SONGNAME 未涵蓋的曲，保留既有手改(SONGNAME 涵蓋的以 SONGNAME 為準)
            keep = prev.get(s) if keep_names else None
            if keep is not None:
                title = keep.get("title", title)
                artist = keep.get("artist", artist)
                src = keep.get("src", src)
                n_kept += 1
            else:
                n_new += 1
        row = {
            "gn": s,
            "fileId": off["fileId"] if off else base["fileId"],
            "bpm": off["bpm"] if off else base["bpm"],
            "src": src,                    # official=官方 songlist / kgn=沿用 k.gn（給你辨識哪些較可信）
            "title": title,
            "artist": artist,
        }
        # 單首 offset（毫秒，補「這首譜跟音檔沒對齊」）——**一律從舊檔原樣搬過來**。
        # 它跟上面那套「歌名該用誰的」判定完全無關（SONGNAME 權不權威跟譜面對不對得齊是兩回事），
        # 而且是人耳一首一首調出來的資料 —— 全量重寫要是把它弄丟，沒有任何地方能還原。
        keep_row = prev.get(s)
        if keep_row and keep_row.get("offsetMs"):
            row["offsetMs"] = keep_row["offsetMs"]
        rows.append(row)

    doc = {
        "schema": "song-name-overrides/2",
        "note": ("手動可編輯的全曲清單(繁體)。這是**唯一**一份手改的歌曲資料——song_catalog.json 是工具從 .gn "
                 "重建的，改它會被蓋掉。key=gn 詞幹(去 k/t)，例 sdom0001，同首 K/T 共用一筆。"
                 "title/artist 覆蓋 k.gn 顯示名，要改名直接改該行。"
                 "offsetMs = 單首 offset(毫秒，補「這首譜跟音檔沒對齊」)：正 = 音樂延後播放，音符不動；"
                 "在譜面編輯器用 F11/F12 邊聽邊調(它不會自動存)，滿意了自己把值寫進這裡。沒寫 = 0。"
                 "機器的音訊延遲不歸它管(那個在 runtime 的譜面時鐘上自動補掉，全部歌一體適用)。"
                 "src=songname 來自遊戲權威歌名表 SONGNAME.TXT(原生繁體，第一順位、歌名+歌手都以它為準)；"
                 "src=official 來自官方 songlist.dat(SONGNAME 未涵蓋的開放曲，簡轉繁)；"
                 "src=kgn 沿用 .gn 內嵌名(可能有錯，請自行校正)；src=manual 是你手改的。"
                 "fileId/bpm/src 僅供辨識，runtime 不套用。SONGNAME 涵蓋的曲重跑一律以 SONGNAME 為準；"
                 "其餘曲的手改會保留(merge-preserve)，--reseed 才整份重填。"
                 "offsetMs 則**一律保留**，重跑/reseed 都不會弄丟。"),
        "count": len(rows),
        "songs": rows,
    }
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(doc, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")

    print(f"完成：{out}")
    print(f"  全曲 {len(rows)} 首（SONGNAME {n_songname}、官方 songlist {n_official}、沿用 k.gn {n_kgn}、人工修正 {n_fixed}）")
    if out.exists():
        print(f"  merge：保留既有手改 {n_kept}、新增 {n_new}" + ("（--reseed：忽略手改重填）" if args.reseed else ""))
    for r in rows[:5]:
        print(f"    {r['gn']:12s} src={r['src']:8s} {r['title']!r} / {r['artist']!r}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
