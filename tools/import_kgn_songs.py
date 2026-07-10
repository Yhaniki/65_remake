# -*- coding: utf-8 -*-
"""
import_kgn_songs.py — 把 k_gn_songlist.csv 新增的歌，把「相關資源」複製進遊戲資料夾。

來源(依 k_gn_keys.csv 的 source 欄)：
  閉撰敃氪  H:/65_remake/assets/閉撰敃氪          (資源大多已解好 ogg/DPS/icon)
  SDO-X     D:/program/SDO-X/SDO-X Alchemist World (音訊多為加密 .sdm，需另解)

遊戲讀取位置(dev/editor，見 SdoExtracted/SongIcons/SongSelectScreen)：
  譜面 .gn      -> assets/sdox_offline/music/<gn>K.gn, <gn>T.gn
  主音樂 .ogg   -> assets/sdox_offline/music/<stem>.ogg              (依詞幹)
  試聽 exper    -> assets/sdox_offline/music/exper/<fileId>.ogg      (依 fileId)
  DANCE .DPS    -> assets/sdox_offline/Extracted/DANCE/<fileId>.DPS  (依 fileId)
  歌曲 icon     -> 直接讀 assets/閉撰敃氪/DatasSDO/UI/MUSIC/ICONS/<fileId>.PNG(dev fallback，不用複製)

本工具只複製「已解好」的資源(gn / 已解 ogg / 已解試聽 / DPS)；加密 .sdm 不在這裡解，
而是輸出兩張待解清單(main_sdm_todo.csv / prev_sdm_todo.csv)給後續的 sdm 解碼步驟。

安全：
  - 只「新增」，若目標 music 已存在該詞幹的譜面(既有歌)→ 整首跳過，絕不覆蓋既有歌。
  - --exclude 檔(預設 no_audio.csv)裡的詞幹整首跳過(那些找不到主音樂，先不加)。

用法:
  python tools/import_kgn_songs.py                 # 實際複製
  python tools/import_kgn_songs.py --dry-run       # 只統計不複製
  python tools/import_kgn_songs.py --include-no-audio   # 連 no_audio 那批也一起加
"""
from __future__ import annotations

import argparse
import csv
import io
import os
import shutil
import sys
from pathlib import Path
from typing import Dict, List, Optional

HERE = Path(__file__).resolve().parent
REPO = HERE.parent

# 來源
BX = Path(r"H:\65_remake\assets\閉撰敃氪")
SX = Path(r"D:\program\SDO-X\SDO-X Alchemist World")
SRC_ROOT = {"閉撰敃氪": BX, "SDO-X Alchemist": SX}

# 目標(遊戲讀取位置，dev/editor)
T_MUSIC = REPO / "assets" / "sdox_offline" / "music"
T_EXPER = T_MUSIC / "exper"
T_DANCE = REPO / "assets" / "sdox_offline" / "Extracted" / "DANCE"

# 資源池(已解好，依 fileId)
POOL_EXPER = BX / "music" / "exper"          # <fileId>.ogg 已解試聽
POOL_DANCE = BX / "DatasSDO" / "DANCE"        # <fileId>.DPS

KGN_DIR = HERE / "kgn_export"
SONGLIST_CSV = KGN_DIR / "k_gn_songlist.csv"
KEYS_CSV = KGN_DIR / "k_gn_keys.csv"


def rd(p: Path) -> List[Dict]:
    return list(csv.DictReader(io.StringIO(p.read_text(encoding="utf-8-sig"))))


def first_existing(*cands: Path) -> Optional[Path]:
    for c in cands:
        if c and c.is_file() and c.stat().st_size > 0:
            return c
    return None


def find_chart(root: Path, gn: str, letter: str) -> Optional[Path]:
    """回傳 root/music/<gn>{K|T}.gn，容忍大小寫。"""
    md = root / "music"
    for name in (f"{gn}{letter.upper()}.gn", f"{gn}{letter.lower()}.gn"):
        p = md / name
        if p.is_file() and p.stat().st_size > 0:
            return p
    return None


def find_main_ogg(root: Path, gn: str) -> Optional[Path]:
    return first_existing(BX / "music" / f"{gn}.ogg",
                          SX / "music_ogg" / f"{gn}.ogg",
                          SX / "music" / f"{gn}.ogg")


def find_main_sdm(gn: str) -> Optional[Path]:
    return first_existing(SX / "music" / f"{gn}.sdm")


def find_prev_ogg(fid: str) -> Optional[Path]:
    return first_existing(POOL_EXPER / f"{fid}.ogg")


def find_prev_sdm(fid: str) -> Optional[Path]:
    return first_existing(SX / "music" / "exper" / f"{fid}.sdm",
                          BX / "music" / "exper" / f"{fid}.sdm")


def find_dps(fid: str) -> Optional[Path]:
    return first_existing(POOL_DANCE / f"{fid}.DPS", POOL_DANCE / f"{fid}.dps")


def copy_new(src: Path, dst: Path, dry: bool, overwrite: bool = False) -> bool:
    """複製 src→dst。dst 已存在且非 overwrite → 不動(回 False)。實際複製回 True。"""
    if dst.exists() and not overwrite:
        return False
    if dry:
        return True
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)
    return True


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="複製新增歌的相關資源進遊戲資料夾")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--exclude", default=str(KGN_DIR / "no_audio.csv"),
                    help="這些詞幹整首跳過(預設 no_audio.csv 那批無主音樂曲)")
    ap.add_argument("--include-no-audio", action="store_true",
                    help="忽略 --exclude，連無主音樂曲也一起加")
    args = ap.parse_args()
    dry = args.dry_run

    song = rd(SONGLIST_CSV)
    src_of = {r["gn"]: r["source"] for r in rd(KEYS_CSV)}

    exclude = set()
    if not args.include_no_audio and Path(args.exclude).is_file():
        for r in rd(Path(args.exclude)):
            if r.get("gn"):
                exclude.add(r["gn"].strip())

    # 既有歌(目標 music 已有的詞幹) → 不覆蓋
    existing_stems = set()
    if T_MUSIC.is_dir():
        for f in T_MUSIC.iterdir():
            n = f.name.lower()
            if n.endswith(".gn"):
                s = n[:-3]
                if s and s[-1] in ("k", "t"):
                    s = s[:-1]
                existing_stems.add(s)

    st = {k: 0 for k in ("songs", "skip_exclude", "skip_existing", "skip_no_src",
                         "chartK", "chartT", "main_ogg", "main_need_sdm", "main_none",
                         "prev_ogg", "prev_need_sdm", "dps", "dps_none")}
    main_todo: List[Dict] = []   # 待解主音樂 sdm
    prev_todo: List[Dict] = []   # 待解試聽 sdm

    for r in song:
        gn = r["gn"].strip()
        fid = (r.get("fileId") or "").strip()
        src = src_of.get(gn)
        root = SRC_ROOT.get(src)
        if gn in exclude:
            st["skip_exclude"] += 1; continue
        if gn in existing_stems:
            st["skip_existing"] += 1; continue
        if root is None:
            st["skip_no_src"] += 1; continue

        ck = find_chart(root, gn, "K")
        ct = find_chart(root, gn, "T")
        if ck is None:                     # 沒 K 譜就不是有效歌，跳過
            st["skip_no_src"] += 1; continue
        st["songs"] += 1

        if copy_new(ck, T_MUSIC / ck.name, dry): st["chartK"] += 1
        if ct and copy_new(ct, T_MUSIC / ct.name, dry): st["chartT"] += 1

        # 主音樂
        mo = find_main_ogg(root, gn)
        if mo:
            copy_new(mo, T_MUSIC / f"{gn}.ogg", dry); st["main_ogg"] += 1
        else:
            ms = find_main_sdm(gn)
            if ms:
                st["main_need_sdm"] += 1
                main_todo.append({"gn": gn, "fileId": fid, "src_sdm": str(ms),
                                  "dst_ogg": str(T_MUSIC / f"{gn}.ogg")})
            else:
                st["main_none"] += 1

        # 試聽
        po = find_prev_ogg(fid)
        if po:
            copy_new(po, T_EXPER / f"{fid}.ogg", dry); st["prev_ogg"] += 1
        else:
            ps = find_prev_sdm(fid)
            if ps:
                st["prev_need_sdm"] += 1
                prev_todo.append({"gn": gn, "fileId": fid, "src_sdm": str(ps),
                                  "dst_ogg": str(T_EXPER / f"{fid}.ogg")})

        # DANCE
        dp = find_dps(fid)
        if dp:
            copy_new(dp, T_DANCE / f"{fid}.DPS", dry); st["dps"] += 1
        else:
            st["dps_none"] += 1

    # 寫待解清單
    if not dry:
        for name, rows in (("main_sdm_todo.csv", main_todo), ("prev_sdm_todo.csv", prev_todo)):
            p = KGN_DIR / name
            with p.open("w", encoding="utf-8-sig", newline="") as f:
                w = csv.DictWriter(f, fieldnames=["gn", "fileId", "src_sdm", "dst_ogg"])
                w.writeheader(); w.writerows(rows)

    print(f"{'[DRY-RUN] ' if dry else ''}加入歌數: {st['songs']}"
          f"  (跳過 exclude={st['skip_exclude']} / 既有={st['skip_existing']} / 無源={st['skip_no_src']})")
    print(f"  譜面: K={st['chartK']}  T={st['chartT']}")
    print(f"  主音樂: 已解ogg複製={st['main_ogg']}  待解sdm={st['main_need_sdm']}  完全無={st['main_none']}")
    print(f"  試聽:   已解ogg複製={st['prev_ogg']}  待解sdm={st['prev_need_sdm']}")
    print(f"  DANCE:  DPS複製={st['dps']}  無(用通用舞蹈)={st['dps_none']}")
    print(f"  待解清單: main_sdm_todo.csv({len(main_todo)})  prev_sdm_todo.csv({len(prev_todo)})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
