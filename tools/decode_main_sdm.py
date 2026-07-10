# -*- coding: utf-8 -*-
"""
decode_main_sdm.py — 用 sdm_to_ogg_ui 的解碼引擎(bms_sdo.sdm_decoder.decode_sdm_to_ogg)把
「待解主音樂清單」main_sdm_todo.csv 的加密 .sdm 批次解成可播 .ogg，並用 ffmpeg 驗證有效。

SDO-X Alchemist(馬來版)主音樂 .sdm 有 songencode.dat 真鑰可還原(非孤兒 keyless)，
decode_sdm_to_ogg 會自動找 songencode + fallback + ffmpeg null-decode 驗證。這裡再加一層
volumedetect：mean_volume 太接近 0dB(≈全雜訊)標記 suspect 供人工複驗(不自動刪有效音樂)。

需要 ffmpeg/ffprobe 在 PATH(本專案在 H:/bms/bak/tools/dist)。並行解(--workers)大幅加速。

用法:
  python tools/decode_main_sdm.py                    # 平行解 main_sdm_todo.csv(略過已存在)
  python tools/decode_main_sdm.py --workers 8 --overwrite
"""
from __future__ import annotations

import argparse
import csv
import io
import os
import re
import subprocess
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

HERE = Path(__file__).resolve().parent
BMS_TOOLS = Path(r"H:\bms\tools")
sys.path.insert(0, str(BMS_TOOLS))

from bms_sdo.sdm_decoder import decode_sdm_to_ogg  # 與 sdm_to_ogg_ui 同一引擎

KGN = HERE / "kgn_export"


def mean_volume_db(ogg: str):
    """回傳 (mean_dB, err)。volumedetect 的 mean_volume 印在 ffmpeg info 級。"""
    try:
        p = subprocess.run(
            ["ffmpeg", "-v", "info", "-hide_banner", "-nostats", "-xerror",
             "-i", ogg, "-af", "volumedetect", "-f", "null", "-"],
            capture_output=True, text=True, timeout=180)
    except Exception as e:
        return None, f"ffmpeg-exc:{e!r}"
    out = (p.stderr or "") + (p.stdout or "")
    m = re.search(r"mean_volume:\s*(-?[0-9.]+) dB", out)
    if not m:
        return None, ("decode-error" if p.returncode != 0 else "no-mean")
    return float(m.group(1)), None


def decode_one(row: dict) -> dict:
    """單檔解碼+驗證(供 ProcessPool 呼叫)。回傳報表列。"""
    gn = row["gn"]; src = row["src_sdm"]; dst = row["dst_ogg"]
    res = {"gn": gn, "fileId": row.get("fileId", ""), "ok": 0, "size": 0, "mean_db": "", "note": "", "src_sdm": src}
    if not os.path.isfile(src):
        res["note"] = "src-missing"; return res
    decoded = False
    try:
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        decoded = bool(decode_sdm_to_ogg(src, dst, is_malaysia=True, log=lambda m: None))
    except Exception as e:
        res["note"] = f"exc:{e!r}"; return res
    if not (decoded and os.path.isfile(dst) and os.path.getsize(dst) > 4096):
        res["note"] = "decode-failed"
        try:
            if os.path.isfile(dst):
                os.remove(dst)
        except OSError:
            pass
        return res
    res["size"] = os.path.getsize(dst)
    mv, err = mean_volume_db(dst)
    if err == "decode-error":
        res["note"] = "decode-error"
        try:
            os.remove(dst)
        except OSError:
            pass
        return res
    res["mean_db"] = "" if mv is None else round(mv, 1)
    res["ok"] = 1
    if mv is not None and mv > -1.5:
        res["note"] = f"suspect-loud({mv:.1f}dB)"   # 保留檔，只標記
    return res


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="批次解主音樂 sdm→ogg + 驗證(平行)")
    ap.add_argument("-i", "--input", default=str(KGN / "main_sdm_todo.csv"))
    ap.add_argument("-r", "--report", default=str(KGN / "main_sdm_report.csv"))
    ap.add_argument("--workers", type=int, default=max(2, (os.cpu_count() or 4) - 2))
    ap.add_argument("--overwrite", action="store_true", help="已存在的 ogg 也重解")
    ap.add_argument("--limit", type=int, default=0)
    args = ap.parse_args()

    rows = list(csv.DictReader(io.StringIO(Path(args.input).read_text(encoding="utf-8-sig"))))
    if args.limit:
        rows = rows[:args.limit]

    rep = []
    todo = []
    for r in rows:
        dst = r["dst_ogg"]
        if not args.overwrite and os.path.isfile(dst) and os.path.getsize(dst) > 4096:
            rep.append({"gn": r["gn"], "fileId": r.get("fileId", ""), "ok": 1,
                        "size": os.path.getsize(dst), "mean_db": "", "note": "exists-skip", "src_sdm": r["src_sdm"]})
        else:
            todo.append(r)
    print(f"待解主音樂 sdm: {len(rows)}  (已存在略過 {len(rep)}，本次解 {len(todo)}，workers={args.workers})")

    ok = bad = 0
    done = 0
    with ProcessPoolExecutor(max_workers=args.workers) as ex:
        futs = [ex.submit(decode_one, r) for r in todo]
        for fut in as_completed(futs):
            res = fut.result()
            rep.append(res)
            done += 1
            if res["ok"]:
                ok += 1
            else:
                bad += 1
            if done % 20 == 0 or done == len(todo):
                print(f"  [{done}/{len(todo)}] ok={ok} bad={bad}", flush=True)

    ok_total = sum(1 for x in rep if x["ok"])
    with Path(args.report).open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["gn", "fileId", "ok", "size", "mean_db", "note", "src_sdm"])
        w.writeheader(); w.writerows(rep)
    print(f"完成：本次成功 {ok} / 失敗 {bad}；含略過總有效 {ok_total}/{len(rows)} -> {args.report}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
