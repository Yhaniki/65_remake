# -*- coding: utf-8 -*-
"""
remove_songs.py — 把指定的幾首歌從遊戲徹底拿掉(檔案 + 歌曲表都清)。

遊戲歌單來自 song_table.csv,而它是由 music/*.gn 譜面產生的。所以「拿掉一首」要同時:
  1) 刪掉該首的資源檔(譜面/主音樂/試聽/DANCE/overlay icon)
  2) 從 song_table.csv 移除該首的每一列(K 譜與 T 譜各一列)
只做 2) 不做 1) 的話,之後重跑 gn_keytable 會又把譜面掃回來 → 復活。故預設兩者都做。

指定歌可用「詞幹 sdomNNNN」、「fileId(數字)」或「歌名(完全相符)」混用,以逗號或換行分隔。

用法:
  python tools/remove_songs.py --songs "sdom5013,15019,Gentleman"
  python tools/remove_songs.py --file to_remove.txt          # 一行一首(詞幹/fileId/歌名)
  python tools/remove_songs.py --songs "sdom5013" --dry-run  # 只看會刪什麼,不動手
  python tools/remove_songs.py --songs "sdom5013" --keep-files  # 只從歌曲表移除,保留檔案(注意:重建會復活)
"""
from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path
from typing import Dict, List, Optional, Set

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import song_table as st  # noqa: E402

REPO = HERE.parent
SA = st.SA

# 歌單/難度/歌名全在這一張表 —— data_root.txt 不轉它,永遠留在 StreamingAssets(隨 build 打包)。
TABLE = st.DEFAULT_CSV

# ---------------------------------------------------------------- 資料樹解析
# 譜面/主音樂/試聽/舞蹈/封面這些「檔案」不在 StreamingAssets,而是遊戲執行期從 data-root 讀。
# data-root 由 runtime 的 SdoDataRoot 解析:env SDO_DATA_ROOT 或 repo 根 data_root.txt 指到別處
# (例如剪枝過的乾淨包 clean\DATA)時,遊戲就從那裡讀 —— 這裡必須跟著寫過去,否則加的歌只進了
# repo 的 sdox_offline、跑起來的遊戲(讀 clean\DATA)完全看不到。見 SdoDataRoot.cs / SdoExtracted.cs。
#
# 一個 data-root 底下四個目錄的位置,完全照 SdoExtracted 的規則推(才不會跟遊戲讀的位置對不上):
#   music = <root>/MUSIC(存在就用)否則 <root 的上一層>/music   ← 對應 SdoExtracted.MusicDir
#   exper = <music>/exper
#   dance = <root>/DANCE                                        ← FrontendApp: "DANCE/<id>.DPS"
#   icons = <root>/UI/MUSIC/ICONS                               ← SongIcons: Root/UI/MUSIC/ICONS
# repo 的 dev 原始樹以 Extracted 當 root:<root>/MUSIC 不存在 → 退到兄弟 sdox_offline/music,
# 剛好等於歷來的四條路徑(下面的 MUSIC/EXPER/DANCE/ICON_OVERLAY 就是它,保留給既有呼叫端)。

DEV_ROOT = REPO / "assets" / "sdox_offline" / "Extracted"


def _tree_for_root(root: Path) -> Dict[str, Path]:
    """給一個 data-root,回它底下 {music,exper,dance,icons} 四個目錄(同 SdoExtracted 規則)。"""
    root = Path(root)
    music = root / "MUSIC"
    if not music.is_dir():
        music = root.parent / "music"          # dev 版型:Extracted 的兄弟 music(遊戲的第二順位)
    return {"music": music, "exper": music / "exper",
            "dance": root / "DANCE", "icons": root / "UI" / "MUSIC" / "ICONS"}


def configured_data_root() -> Optional[Path]:
    """覆寫來源(同 runtime SdoDataRoot.ConfiguredRoot):env SDO_DATA_ROOT 優先,否則 repo 根
    data_root.txt 第一行。指到「存在的別處」才回,否則 None(打錯字的路徑不會被拿來亂建樹)。"""
    try:
        e = (os.environ.get("SDO_DATA_ROOT") or "").strip()
        if e and Path(e).is_dir():
            return Path(e)
    except Exception:
        pass
    try:
        f = REPO / "data_root.txt"
        if f.is_file():
            for ln in f.read_text(encoding="utf-8").splitlines():
                ln = ln.strip()
                if ln:
                    return Path(ln) if Path(ln).is_dir() else None
    except Exception:
        pass
    return None


def _norm(p: Path) -> str:
    try:
        return os.path.normcase(os.path.abspath(str(p)))
    except Exception:
        return str(p)


def data_trees() -> List[Dict[str, Path]]:
    """所有要同步的資料樹(去重):dev 原始樹永遠在;若 data_root.txt/SDO_DATA_ROOT 指到別處
    (遊戲實際讀的乾淨 DATA),也一起寫。順序 = [dev, ...configured];runtime_tree 取最後一個。"""
    trees: List[Dict[str, Path]] = []
    seen = set()
    for root in (DEV_ROOT, configured_data_root()):
        if root is None:
            continue
        t = _tree_for_root(root)
        key = (_norm(t["music"]), _norm(t["dance"]), _norm(t["icons"]))
        if key in seen:
            continue
        seen.add(key)
        trees.append(t)
    return trees


def runtime_tree() -> Dict[str, Path]:
    """遊戲執行期實際讀檔的那棵樹(有覆寫就是覆寫的,否則 dev)。用來判斷資源在不在位。"""
    return data_trees()[-1]


# 保留給既有呼叫端的單棵 dev-tree 常數(= data_trees()[0]);多樹同步走上面的 data_trees()。
_DEV = _tree_for_root(DEV_ROOT)
MUSIC = _DEV["music"]
EXPER = _DEV["exper"]
DANCE = _DEV["dance"]
ICON_OVERLAY = _DEV["icons"]


def stem(gn: str) -> str:
    g = (gn or "").lower().rsplit("/", 1)[-1].rsplit("\\", 1)[-1]
    if g.endswith(".gn"):
        g = g[:-3]
    if g and g[-1] in ("k", "t"):
        g = g[:-1]
    return g


def load_catalog():
    """{stem: {fileId, title, gns[]}}. fileId/title 取「K 譜」那列 — 遊戲顯示與資源(DANCE/試聽/icon)
    都用 K 譜的 fileId(K=15013、T=5013 這種 K/T fileId 不一致的歌,一律以 K 為準)。"""
    by_stem: Dict[str, Dict] = {}
    for r in st.load(TABLE):
        s = stem(r["gn"])
        if not s:
            continue
        is_k = st.is_primary(r["gn"])
        cur = by_stem.get(s)
        if cur is None:
            by_stem[s] = {"fileId": int(r.get("fileId") or 0), "title": r.get("title") or "",
                          "gns": [r["gn"]], "_k": is_k}
        else:
            cur["gns"].append(r["gn"])
            if is_k and not cur["_k"]:          # 以 K 譜的 fileId/title 為準
                cur["fileId"] = int(r.get("fileId") or 0); cur["title"] = r.get("title") or ""; cur["_k"] = True
    for v in by_stem.values():
        v.pop("_k", None)
    return by_stem


def resolve(tokens: List[str], by_stem: Dict[str, Dict]):
    """把使用者給的 token(詞幹/fileId/歌名)解析成一組詞幹。回傳(stems, unresolved, ambiguous)。"""
    fid_to_stem = {v["fileId"]: k for k, v in by_stem.items()}
    title_to_stems: Dict[str, List[str]] = {}
    for k, v in by_stem.items():
        title_to_stems.setdefault((v["title"] or "").strip().lower(), []).append(k)

    stems: Set[str] = set()
    unresolved: List[str] = []
    ambiguous: Dict[str, List[str]] = {}
    for t in tokens:
        t = t.strip()
        if not t:
            continue
        low = t.lower()
        if re.fullmatch(r"sdom[\d_]+", low):              # 詞幹(插隊加的歌會有 _N 後綴，例 sdom1150_1)
            (stems.add(low) if low in by_stem else unresolved.append(t))
        elif t.isdigit():                                  # fileId
            fid = int(t)
            (stems.add(fid_to_stem[fid]) if fid in fid_to_stem else unresolved.append(t))
        else:                                              # 歌名(完全相符)
            hits = title_to_stems.get(low, [])
            if len(hits) == 1:
                stems.add(hits[0])
            elif len(hits) > 1:
                ambiguous[t] = hits
            else:
                unresolved.append(t)
    return stems, unresolved, ambiguous


def prune_table(target: Set[str]) -> int:
    """從 song_table.csv 移除這些詞幹的每一列(K/T 各一)。回傳移除的列數。"""
    rows = st.load(TABLE)
    keep = [r for r in rows if stem(r["gn"]) not in target]
    st.save(keep, TABLE)
    return len(rows) - len(keep)


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="從遊戲移除指定歌曲")
    ap.add_argument("--songs", default="", help="逗號分隔:詞幹 sdomNNNN / fileId / 歌名")
    ap.add_argument("--file", help="一行一首的清單檔")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--keep-files", action="store_true", help="只清歌曲表、保留檔案(重建會復活)")
    args = ap.parse_args()

    tokens: List[str] = []
    if args.songs:
        tokens += re.split(r"[,\n]", args.songs)
    if args.file:
        tokens += Path(args.file).read_text(encoding="utf-8-sig").splitlines()

    by_stem = load_catalog()
    stems, unresolved, ambiguous = resolve(tokens, by_stem)

    if ambiguous:
        print("歌名對到多首,請改用詞幹或 fileId 指定:")
        for t, hits in ambiguous.items():
            print(f"  「{t}」-> " + ", ".join(f"{h}(id={by_stem[h]['fileId']})" for h in hits))
    if unresolved:
        print("找不到(略過):", ", ".join(unresolved))
    if not stems:
        print("沒有可移除的目標。"); return 1

    print(f"將移除 {len(stems)} 首:")
    for s in sorted(stems):
        v = by_stem[s]
        print(f"  {s}  id={v['fileId']}  {v['title']!r}  charts={v['gns']}")

    remove_stems(stems, by_stem, dry_run=args.dry_run, keep_files=args.keep_files)
    return 0


def remove_stems(stems: Set[str], by_stem: Dict[str, Dict], dry_run: bool = False, keep_files: bool = False):
    """核心:刪掉這些詞幹的資源檔 + 從 song_table.csv 移除那幾列。sync 工具也呼叫這支。"""
    to_delete: List[Path] = []
    for s in stems:
        v = by_stem[s]; fid = v["fileId"]
        for tree in data_trees():                      # dev 樹 + data_root.txt 指到的乾淨 DATA,都要清
            for gn in v["gns"]:
                to_delete.append(tree["music"] / gn)
            to_delete += [tree["music"] / f"{s}.ogg", tree["exper"] / f"{fid}.ogg",
                          tree["dance"] / f"{fid}.DPS", tree["icons"] / f"{fid}.PNG",
                          tree["icons"] / f"{fid}.png"]

    if dry_run:
        print("\n[DRY-RUN] 會刪這些檔(存在者):")
        for p in to_delete:
            if p.exists():
                print("   -", p)
        print("[DRY-RUN] 會從 song_table.csv 移除這些詞幹的列;不實際變更。")
        return

    ndel = 0
    if not keep_files:
        for p in to_delete:
            try:
                if p.is_file():
                    p.unlink(); ndel += 1
            except OSError as e:
                print(f"  刪不掉 {p}: {e}", file=sys.stderr)

    removed = prune_table(stems)

    print(f"\n完成:刪檔 {ndel}{'(--keep-files 略過)' if keep_files else ''};"
          f"song_table.csv 移除 {removed} 列")
    print("遊戲下次啟動即不再出現這些歌。")


if __name__ == "__main__":
    raise SystemExit(main())
