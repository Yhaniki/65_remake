# -*- coding: utf-8 -*-
"""
build_song_name_overrides.py — 把「權威歌名」填進 song_table.csv 的 title / artist 欄。

為什麼:
  遊戲歌名原本直接取自各 .gn 內嵌 StepFile 表頭(k.gn)，但有些 .gn 表頭的歌名/歌手是錯的
  (檔位被回收、metadata 沒更新)。改成讓遊戲讀這份清單顯示歌名，方便你隨時就地改名。

  涵蓋 song_table.csv 裡的每一首歌(以 gn 詞幹 sdomNNNN 為單位，同首 K/T 兩列共用同一組歌名)，
  每首 title/artist 預先填好:
    - 開放曲(在官方 songlist.dat 且列於 open_list.txt)      -> 用 songlist.dat 的權威歌名
    - 其餘(未開放 / songlist 未收錄 / 教學曲…)               -> 用該曲 k.gn 的名字當預設
  未開放曲「不採用」songlist.dat 的名字(那些檔位被回收、資訊不對)，改用 k.gn 的名字打底。
  之後任何一首名字要改，直接編輯 song_table.csv 那一列的 title / artist 即可。

  只動顯示名(title/artist/bpm/src)；難度/音符數/解密欄位全部不碰(那些是譜面本身的資料)。

offsetMs(每首歌的音訊校正，唯一會真的進遊戲的欄位):
  某些歌的 .ogg 前面多/少了一小段(轉檔留的空白、來源本身就沒對準)，音樂就跟譜面差個幾十毫秒。
  在那首填 offsetMs 即可校正:正值 = 音樂晚一點進來(音樂跑在譜面前面、音符老是慢半拍時用)、
  負值 = 音樂提早、0/不填 = 不動(絕大多數歌都是 0)。它疊在譜面自己的音樂起點(type-10 marker)上，
  只挪音樂＋舞蹈，音符與判定仍釘在譜面時鐘 → 調錯頂多音畫不合拍，不會改難度。範圍 ±5000ms。

重建保護(重要):
  預設為「merge-preserve」— 重跑時**保留你已手改的每一筆**，只補上新出現的歌。所以之後新增
  歌曲後可安心重跑。要「整份由來源重新填、丟棄手改」才需要 --reseed。
  offsetMs 是例外:它沒有任何來源可重建(只有人耳調得出來)，所以連 --reseed 都會保留。
  bpm 同理只在空的時候填(它是可手改的顯示值)。

SONGNAME.TXT 為什麼預設關掉:
  那是**原版遊戲自己的**顯示歌名表(`H:\\sdo\\熱舞 Online(金富貴寶寶)\\DATA\\SONGNAME.TXT`，Big5、
  575 首、格式 `id,flag,歌名,歌手`)，不在本 repo 裡，遊戲執行期也從不讀它 —— 它的用途只有一次性
  「把官方歌名灌進表」。灌完之後名字就活在 song_table.csv 裡了。
  但它是**無條件覆蓋**的(merge-preserve 保護不到，那是它身為權威表的定義)，所以只要它還開著，
  每次重跑都會把你手改過的那 575 首打回官方版本 —— 這就是以前「周杰倫→周傑倫、七里香→七裏香」
  的成因。既然它的活已經幹完了，預設就別再讀。真的要重新灌一次官方名才加 --songname。
  它有錯字、而 .gn 表頭才對的那幾首，釘在 KNOWN_CORRECTIONS(最高優先，連 --songname 也蓋不掉)。

編碼/繁體:
  songlist.dat 內嵌文字是 GB2312/GBK(簡中)；本工具 dev 端用 gb18030 解一次寫成 UTF-8(無 BOM)。
  歌名再以 opencc s2twp 由簡轉繁(台灣用字)；英文、日文(含假名)、已是繁體的字串一律原樣不動
  (避免 范→範、群→羣 這類對已繁體文字的誤轉)。需 `pip install opencc-python-reimplemented`；
  缺套件時原樣輸出(簡中)並提示。

用法:
  python tools/build_song_name_overrides.py            # merge-preserve(保留手改, 只補新歌的名字)
  python tools/build_song_name_overrides.py --songname # 另外用原版 SONGNAME.TXT 覆蓋那 575 首
  python tools/build_song_name_overrides.py --reseed   # 歌名整批由 songlist.dat + k.gn 重填(丟棄手改!)
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
sys.path.insert(0, str(HERE))
import song_table as st  # noqa: E402

REPO = HERE.parent
SA = REPO / "65" / "My project" / "Assets" / "StreamingAssets"
DEFAULT_TABLE = st.DEFAULT_CSV
DEFAULT_SONGLIST = Path(r"H:\sdo_tw\15熱舞 Online(金富貴寶寶)\Music\songlist.dat")
DEFAULT_OPENLIST = Path(r"H:\sdo_tw\15熱舞 Online(金富貴寶寶)\Music\open_list.txt")

# songlist.dat 版面(逆向確認): u16 count @0, 之後每筆 756 bytes。內嵌 300-byte StepFile 表頭 @ +456。
#   gn 檔名 @+0(32B)；表頭欄位(對齊 build_gn_header_catalog.py): fileId@0, bpm f32@16,
#   title@108(32B), artist(=writer)@172(32B)
REC_SIZE = 756
HDR_OFF = 456

# SONGNAME.TXT = 遊戲實際顯示用的權威歌名表(逗號分隔 `id,flag,歌名,歌手`，Big5/big5hkscs 編碼、
# 原生繁體)。id = 曲號(sdomNNNN 的 NNNN)。這是第一順位來源：涵蓋到的曲一律以它為準(歌名+歌手)，
# 且是原生繁體不需 opencc。之前佔位/亂碼(975/9you/血淚remix…)的真名都在這裡。
DEFAULT_SONGNAME = Path(r"H:\sdo\熱舞 Online(金富貴寶寶)\DATA\SONGNAME.TXT")

# 人工修正表 = **最高優先，蓋過 SONGNAME.TXT**。
# SONGNAME 是「遊戲顯示用」的權威表，但它自己也有錯字；.gn 表頭反而是對的。這種曲只能在這裡釘死，
# 因為 SONGNAME 涵蓋到的曲每次重跑都會被它覆蓋（merge-preserve 保護不到），改 song_table.csv 沒用。
KNOWN_CORRECTIONS: Dict[str, Tuple[str, str]] = {
    # SONGNAME 寫「有膽你就來」/「愛的主題秀」，但 .gn 表頭(titleZhCn)是「好膽你就來」/「愛的主場秀」——採表頭。
    "sdom1947": ("好膽你就來", "張惠妹"),
    "sdom1953": ("愛的主場秀", "羅志祥"),
}


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


def table_by_stem(rows: List[Dict]) -> Tuple[List[str], Dict[str, Dict]]:
    """song_table 的列 → (依 fileId 由大到小排序的 stem 清單, {stem: 該首的打底/現值})。

    每個 stem 取其 K 譜那一列(顯示用的主譜)，缺則取任一。打底名字用 titleZhCn/artistZhCn
    (.gn 表頭原字)，不是現在的 title —— 現在的 title 是「已經處理過的顯示名」，
    拿它再跑一次 to_traditional 等於把人手改的結果又轉一次。"""
    by: Dict[str, Dict] = {}
    for r in rows:
        s = stem(r["gn"])
        if not s:
            continue
        is_k = st.is_primary(r["gn"])
        if by.get(s) is None or (is_k and not by[s]["_is_k"]):
            by[s] = {"title": r.get("titleZhCn") or "", "artist": r.get("artistZhCn") or "",
                     "fileId": int(r.get("fileId") or 0),
                     "bpm": round(float(r.get("chartBpm") or 0.0), 3),
                     "cur": {"title": r.get("title") or "", "artist": r.get("artist") or "",
                             "src": r.get("src") or ""},
                     "_is_k": is_k}
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
    ap = argparse.ArgumentParser(description="把權威歌名填進 song_table.csv 的 title / artist")
    ap.add_argument("table", nargs="?", default=str(DEFAULT_TABLE))
    ap.add_argument("songlist", nargs="?", default=str(DEFAULT_SONGLIST))
    ap.add_argument("openlist", nargs="?", default=str(DEFAULT_OPENLIST))
    # SONGNAME.TXT **預設不讀**（見型別註解的「為什麼預設關掉」）。要重新灌一次官方歌名才給路徑，
    # 給 --songname 不帶值就是用原版安裝的預設位置。
    ap.add_argument("--songname", nargs="?", const=str(DEFAULT_SONGNAME), default="",
                    help=f"用 SONGNAME.TXT(原版權威歌名表)覆蓋歌名；不帶值 = {DEFAULT_SONGNAME}")
    ap.add_argument("--reseed", action="store_true",
                    help="整份由來源重填，丟棄既有手改(預設會保留 SONGNAME 未涵蓋曲的手改；offsetMs 一律保留)")
    args = ap.parse_args()

    table_path = Path(args.table)
    songlist = Path(args.songlist)
    openlist = Path(args.openlist)
    rows = st.load(table_path)
    if not rows:
        print(f"找不到/讀不到 {table_path}（先跑 tools/gn_keytable.py + build_gn_header_catalog.py）",
              file=sys.stderr); return 1

    if _S2TWP is None:
        print("[warn] 未安裝 opencc-python-reimplemented，歌名暫以簡中原字輸出；"
              "pip install opencc-python-reimplemented 後重跑可轉繁體。", file=sys.stderr)

    if not args.songname:
        songname: Dict[str, Tuple[str, str]] = {}
        print("[ok] 沒讀 SONGNAME.TXT（歌名以表裡現有的為準；要重灌官方名加 --songname）")
    else:
        songname = parse_songname(Path(args.songname))
        if songname:
            print(f"[ok] SONGNAME.TXT 權威歌名表：{len(songname)} 首（**會覆蓋表裡的手改歌名**）")
        else:
            print(f"[warn] 讀不到 SONGNAME.TXT（{args.songname}），改用 songlist/k.gn。", file=sys.stderr)

    official = parse_songlist_open(songlist, openlist) if (songlist.is_file() and openlist.is_file()) else {}
    if not official:
        print(f"[warn] 讀不到 songlist.dat/open_list.txt（{songlist}），全部用 k.gn 名字打底。", file=sys.stderr)

    order, kgn = table_by_stem(rows)

    # merge-preserve：表裡現有的 title/artist/src 就是「上一輪的結果 + 人手改過的東西」。
    # --reseed 時丟棄(整批由來源重填)。offsetMs 本工具從頭到尾一個字都不寫 —— 那欄沒有來源
    # 可重建(只有人耳調得出來)，不碰就不會弄丟。
    prev: Dict[str, Dict] = {} if args.reseed else {s: kgn[s]["cur"] for s in kgn}

    out_rows: List[Dict] = []
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
            # merge-preserve：SONGNAME 未涵蓋的曲，保留既有手改(SONGNAME 涵蓋的以 SONGNAME 為準)
            keep = prev.get(s)
            if keep and (keep.get("title") or keep.get("artist")):
                title = keep.get("title") or title
                artist = keep.get("artist") or artist
                src = keep.get("src") or src
                n_kept += 1
            else:
                n_new += 1
        # 人工修正最後套，**連 SONGNAME 也蓋** —— SONGNAME 自己有錯字的那幾首只能在這裡釘死
        # （它涵蓋到的曲每次重跑都會被它覆蓋，merge-preserve 保護不到，改 csv 也會被打回原樣）。
        fix = KNOWN_CORRECTIONS.get(s)
        if fix:
            if src == "songname":
                n_songname -= 1
            title, artist, src = fix[0], fix[1], "fixed"
            n_fixed += 1
        out_rows.append({
            "gn": s,
            "bpm": off["bpm"] if off else base["bpm"],
            "src": src,                    # official=官方 songlist / kgn=沿用 k.gn（給你辨識哪些較可信）
            "title": title,
            "artist": artist,
        })

    # 寫回：一個詞幹的 K/T 兩列共用同一組顯示名（st.save 也會再同步一次）。
    # 只碰 title/artist/bpm/src 四欄 —— fileId、難度、offsetMs、解密欄位一律原樣不動。
    by_stem: Dict[str, List[Dict]] = {}
    for r in rows:
        by_stem.setdefault(stem(r["gn"]), []).append(r)
    n_bpm = 0
    for o in out_rows:
        for r in by_stem.get(o["gn"], []):
            r["title"], r["artist"], r["src"] = o["title"], o["artist"], o["src"]
            # bpm 跟歌名一樣是可手改的顯示值 → **只在空的時候填**（--reseed 才由來源重填）。
            # 以前是無條件覆蓋，於是每次重跑都把人調過的顯示 BPM 打回表頭原值。
            if o["bpm"] and (args.reseed or not (r.get("bpm") or 0) > 0):
                if r.get("bpm") != o["bpm"]:
                    n_bpm += 1
                r["bpm"] = o["bpm"]
    st.save(rows, table_path)

    print(f"完成：{table_path}")
    print(f"  全曲 {len(out_rows)} 首（SONGNAME {n_songname}、官方 songlist {n_official}、"
          f"沿用 k.gn {n_kgn}、人工修正 {n_fixed}）")
    print(f"  merge：保留既有手改 {n_kept}、新填 {n_new}" + ("（--reseed：忽略手改重填）" if args.reseed else ""))
    print(f"  bpm：填了 {n_bpm} 首（本來就有值的不動）")
    print("  offsetMs 完全沒動（那欄無來源可重建，只有人耳調得出來）")
    for r in out_rows[:5]:
        print(f"    {r['gn']:12s} src={r['src']:8s} {r['title']!r} / {r['artist']!r}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
