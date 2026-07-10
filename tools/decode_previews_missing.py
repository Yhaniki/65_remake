# -*- coding: utf-8 -*-
"""
decode_previews_missing.py — 只解「缺的」試聽 sdm（prev_sdm_todo.csv 那 367 首），非破壞式。

decode_previews.py 會先刪光整個 exper/*.ogg 再全量重建；我們已把 1149 首已解好的試聽直接複製到
sdox_offline/music/exper，不能被刪。這支只針對待解清單裡「目標還沒有 ogg」的 fileId，沿用
decode_previews 的 donor-header 解碼 + ffprobe 驗證，解出來寫進 exper，其餘一律不動。

用法(需 ffmpeg/ffprobe 在 PATH):
  python tools/decode_previews_missing.py
  python tools/decode_previews_missing.py -i tools/kgn_export/prev_sdm_todo.csv
"""
from __future__ import annotations

import argparse
import csv
import io
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

# 沿用 decode_previews 的解碼/驗證/donor 建庫(import 不會執行其 main)
from decode_previews import build_donor_index, decode, valid, find_sdm, SIG  # noqa: E402

KGN = HERE / "kgn_export"


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="只解缺的試聽 sdm(非破壞式)")
    ap.add_argument("-i", "--input", default=str(KGN / "prev_sdm_todo.csv"))
    ap.add_argument("-r", "--report", default=str(KGN / "prev_sdm_report.csv"))
    ap.add_argument("--overwrite", action="store_true", help="目標已有 ogg 也重解(預設略過)")
    args = ap.parse_args()

    rows = list(csv.DictReader(io.StringIO(Path(args.input).read_text(encoding="utf-8-sig"))))
    exper_sig, main_sig = build_donor_index()
    print(f"donor library: exper codebooks={len(exper_sig)}  main codebooks={len(main_sig)} (sig={SIG}B)")
    print(f"待解試聽 sdm: {len(rows)}")

    ok = skip = nosdm = nodonor = bad = 0
    rep = []
    tmp = str(KGN / "_prevtmp.ogg")
    for i, r in enumerate(rows, 1):
        fid = (r.get("fileId") or "").strip()
        dst = Path(r["dst_ogg"])
        note = ""
        if dst.is_file() and not args.overwrite:
            skip += 1; rep.append({"fileId": fid, "ok": 1, "note": "exists-skip"}); continue
        sdm = r.get("src_sdm") or ""
        if not (sdm and os.path.isfile(sdm)):
            sdm = find_sdm(fid) or ""    # fallback：照 fileId 找
        if not sdm:
            nosdm += 1; note = "no-sdm"
        else:
            out, cs, tier = decode(open(sdm, "rb").read(), exper_sig, main_sig)
            if out is None:
                nodonor += 1; note = f"no-codebook-donor(bestSuffix={cs})"
            else:
                open(tmp, "wb").write(out)
                if valid(tmp):
                    dst.parent.mkdir(parents=True, exist_ok=True)
                    os.replace(tmp, str(dst)); ok += 1; note = f"ok({tier})"
                else:
                    bad += 1; note = f"ffprobe-invalid({tier})"
        rep.append({"fileId": fid, "ok": int(note.startswith("ok") or note == "exists-skip"), "note": note})
        if i % 50 == 0 or i == len(rows):
            print(f"  [{i}/{len(rows)}] ok={ok} skip={skip} no-sdm={nosdm} no-donor={nodonor} bad={bad}", flush=True)
    if os.path.isfile(tmp):
        os.remove(tmp)

    with Path(args.report).open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["fileId", "ok", "note"]); w.writeheader(); w.writerows(rep)
    print(f"完成：解出 {ok}、已存在略過 {skip}、無sdm {nosdm}、無donor {nodonor}、壞 {bad} -> {args.report}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
