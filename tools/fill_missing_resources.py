# -*- coding: utf-8 -*-
"""
fill_missing_resources.py — 用新到位的來源補齊 missing_resources.csv 的缺口。

來源(使用者提供)：
  SDO-X DATA   D:/program/SDO-X/SDO-X Alchemist World/DATA/DANCE(*.DPS)、/UI/MUSIC/ICONS(*.PNG,少數 *.DDS)
  閉撰 ICONS   assets/閉撰敃氪/DatasSDO/UI/MUSIC/ICONS(有些只有 *.DDS 沒 *.PNG → 轉 PNG)
  CiB 備份     D:/backup/20221212backup/CiB Net Station/SDO-X Global OBT/music/exper(*.ogg 試聽)

目標(遊戲讀取)：
  DANCE  -> assets/sdox_offline/Extracted/DANCE/<fileId>.DPS
  ICON   -> assets/sdox_offline/Extracted/UI/MUSIC/ICONS/<fileId>.PNG   (SongIcons 第一順位 Root/UI/MUSIC/ICONS)
  試聽   -> assets/sdox_offline/music/exper/<fileId>.ogg

icon 只吃 PNG(SongIcons.Load 只找 .PNG/.png)；DDS 用 PIL 轉 RGBA PNG。
剩下補不到的：DANCE→通用舞蹈、ICON→預設圖、試聽→遊戲內主音樂中段 20s loop(見 SongSelectScreen)。

用法: python tools/fill_missing_resources.py [--dry-run]
"""
from __future__ import annotations

import argparse
import csv
import io
import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
KG = HERE / "kgn_export"

SXD = Path(r"D:\program\SDO-X\SDO-X Alchemist World\DATA")
SX_DANCE = SXD / "DANCE"
SX_ICON = SXD / "UI" / "MUSIC" / "ICONS"
BX_ICON = REPO / "assets" / "閉撰敃氪" / "DatasSDO" / "UI" / "MUSIC" / "ICONS"
CIB = Path(r"D:\backup\20221212backup\CiB Net Station\SDO-X Global OBT\music\exper")

T_DANCE = REPO / "assets" / "sdox_offline" / "Extracted" / "DANCE"
T_ICON = REPO / "assets" / "sdox_offline" / "Extracted" / "UI" / "MUSIC" / "ICONS"
T_EXPER = REPO / "assets" / "sdox_offline" / "music" / "exper"


def firstfile(*cs: Path):
    for c in cs:
        if c and c.is_file() and c.stat().st_size > 0:
            return c
    return None


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    dry = args.dry_run

    miss = list(csv.DictReader(io.StringIO((KG / "missing_resources.csv").read_text(encoding="utf-8-sig"))))
    for d in (T_DANCE, T_ICON, T_EXPER):
        if not dry:
            d.mkdir(parents=True, exist_ok=True)

    Image = None
    st = {k: 0 for k in ("dance_ok", "dance_no", "icon_png", "icon_dds", "icon_no", "prev_ok", "prev_no")}
    still = {"dance": [], "icon": [], "preview": []}

    for r in miss:
        fid = r["fileId"].strip()
        m = r["missing"]

        if "dance" in m:
            src = firstfile(SX_DANCE / f"{fid}.DPS", SX_DANCE / f"{fid}.dps")
            if src:
                if not dry:
                    shutil.copy2(src, T_DANCE / f"{fid}.DPS")
                st["dance_ok"] += 1
            else:
                st["dance_no"] += 1; still["dance"].append(fid)

        if "icon" in m:
            png = firstfile(SX_ICON / f"{fid}.PNG", SX_ICON / f"{fid}.png")
            dds = firstfile(BX_ICON / f"{fid}.DDS", SX_ICON / f"{fid}.DDS")
            if png:
                if not dry:
                    shutil.copy2(png, T_ICON / f"{fid}.PNG")
                st["icon_png"] += 1
            elif dds:
                if not dry:
                    if Image is None:
                        from PIL import Image as _I
                        Image = _I
                    im = Image.open(dds).convert("RGBA")
                    im.save(T_ICON / f"{fid}.PNG")
                st["icon_dds"] += 1
            else:
                st["icon_no"] += 1; still["icon"].append(fid)

        if "preview" in m:
            src = firstfile(CIB / f"{fid}.ogg")
            if src:
                if not dry:
                    shutil.copy2(src, T_EXPER / f"{fid}.ogg")
                st["prev_ok"] += 1
            else:
                st["prev_no"] += 1; still["preview"].append(fid)

    print(f"{'[DRY] ' if dry else ''}補齊結果:")
    print(f"  DANCE : 補 {st['dance_ok']}  仍缺 {st['dance_no']}(→通用舞蹈)")
    print(f"  ICON  : PNG複製 {st['icon_png']} + DDS轉PNG {st['icon_dds']}  仍缺 {st['icon_no']}(→預設圖)")
    print(f"  試聽  : 補 {st['prev_ok']}  仍缺 {st['prev_no']}(→遊戲內主音樂中段20s loop)")
    # 記錄仍缺清單供人工/後續參考
    if not dry:
        with (KG / "still_missing.csv").open("w", encoding="utf-8-sig", newline="") as f:
            w = csv.writer(f); w.writerow(["fileId", "kind"])
            for k, ids in still.items():
                for i in ids:
                    w.writerow([i, k])
        print(f"  仍缺清單 -> {KG / 'still_missing.csv'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
