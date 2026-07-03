#!/usr/bin/env python3
"""
Build OPTIONDLG.clean.png — the official OPTION-dialog atlas with all baked-in
Chinese text painted out, so the remake can overlay dynamic (TMP) localized text
on the exact official pink frame.

Two removal techniques, chosen by what the text sits on:
  * white text on a magenta pill/bar (title, save/exit/default buttons):
    per-column VERTICAL interpolation between the pill's clean top/bottom edges —
    wipes the glyphs while preserving the vertical gloss and rounded caps.
  * board interiors (section-header pills, option labels, radio dots, MIN/MAX):
    per-row median-background flatten — rebuilds each row from its dominant light
    panel colour, leaving a clean flat sunken panel. The remake re-draws the
    interactive bits (radio dots / slider handle / arrow keys already exist as
    standalone clean sprites in the atlas top-right).

Everything else (outer frame, gloss, light dots) is left untouched.

Usage:  python tools/build_optiondlg_clean.py [--debug]
Output: <atlas dir>/OPTIONDLG.clean.png  (beside the source OPTIONDLG.PNG)
"""
import os, sys
import numpy as np
import cv2
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ATLAS_DIR = os.path.join(ROOT, "assets", "閉撰敃氪", "DatasSDO", "UI", "OPTIONDLG")
SRC = os.path.join(ATLAS_DIR, "OPTIONDLG.PNG")
DST = os.path.join(ATLAS_DIR, "OPTIONDLG.clean.png")

# atlas regions (x, y, w, h) — from the OPTIONDLG .an crop table.
# Only the boards reused as flat inner panels are flattened. The keyboard boards
# (Opt6K/Drum/7K) are LEFT INTACT because their cyan direction-arrow sprites are
# baked in and reused by the faithful key-binding tab; their Chinese title/hint
# strips are removed separately (see KBOARD_TEXT_STRIPS).
BOARDS = [
    (324, 384, 350, 207),   # OptVolumeBoard  -> audio tab inner panel
    (674, 384, 350, 206),   # OptScreenBoard  -> video/display tab inner panel
]

# keyboard boards: flatten only the top title strip and bottom hint strip,
# preserving the middle arrow band. (board x,y,w,h, top-strip h, bottom-strip h)
KBOARDS = [
    (0, 486, 322, 163),     # Opt6KBoard
    (0, 649, 322, 163),     # OptDrumBoard
    (0, 812, 322, 163),     # Opt7KBoard (4-key)
]
TITLE_TEXT = (361, 8, 150, 26)                       # 选项 OPTION
BUTTONS = [(742, 0), (743, 36), (743, 72),           # save/exit/default,
           (743, 108), (744, 144), (744, 180)]       #   both visual states


def flatten_board(out, alpha, x, y, w, h, inset=6):
    x += inset; y += inset; w -= 2 * inset; h -= 2 * inset
    for yy in range(y, y + h):
        a = alpha[yy, x:x + w]
        idx = np.where(a > 8)[0]
        if len(idx) < 6:
            continue
        px = out[yy, x:x + w, :3][idx].astype(np.float32)
        L = 0.299 * px[:, 0] + 0.587 * px[:, 1] + 0.114 * px[:, 2]
        sat = px.max(1) - px.min(1)
        sel = (L > 172) & (sat < 62)                 # light, low-saturation panel bg
        cand = px[sel] if sel.sum() >= 4 else px
        c = (np.median(cand, 0) + 0.5).astype(np.uint8)
        out[yy, x:x + w, :3][idx] = c


def _scan_v(alpha, xx, yy, dy):
    H = alpha.shape[0]
    for _ in range(30):
        yy += dy
        if yy < 0 or yy >= H:
            return None
        if alpha[yy, xx] > 8:
            return yy
    return None


def fill_v(out, alpha, x, y, w, h):
    for xx in range(x, x + w):
        ty = _scan_v(alpha, xx, y, -1)
        by = _scan_v(alpha, xx, y + h - 1, +1)
        if ty is None or by is None or by - ty < 2:
            continue
        ct = out[ty, xx, :3].astype(np.float32)
        cb = out[by, xx, :3].astype(np.float32)
        for yy in range(ty, by + 1):
            if alpha[yy, xx] <= 8:
                continue
            t = (yy - ty) / (by - ty)
            out[yy, xx, :3] = (ct * (1 - t) + cb * t + 0.5).astype(np.uint8)


def main():
    debug = "--debug" in sys.argv
    if not os.path.exists(SRC):
        sys.exit("source atlas not found: " + SRC)
    arr = np.array(Image.open(SRC).convert("RGBA"))
    out = arr.copy()
    alpha = arr[:, :, 3]

    for r in BOARDS:
        flatten_board(out, alpha, *r)
    fill_v(out, alpha, *TITLE_TEXT)
    for (bx, by) in BUTTONS:
        fill_v(out, alpha, bx + 7, by + 7, 82, 20)

    Image.fromarray(out, "RGBA").save(DST)
    print("wrote", DST)

    if debug:
        cim = Image.fromarray(out, "RGBA")
        dbg = os.path.join(HERE, "_optiondlg_debug")
        os.makedirs(dbg, exist_ok=True)
        cv = Image.new("RGBA", (800, 600), (60, 60, 70, 255))
        for src, dst in [((355, 0, 256, 256), (220, 128)),
                         ((611, 0, 133, 256), (476, 128)),
                         ((355, 256, 256, 120), (220, 384)),
                         ((611, 256, 138, 120), (476, 384)),
                         ((674, 384, 350, 206), (236, 225)),
                         ((744, 144, 95, 33), (261, 448)),
                         ((742, 0, 95, 33), (368, 448)),
                         ((743, 72, 95, 33), (475, 448))]:
            x, y, w, h = src
            cv.alpha_composite(cim.crop((x, y, x + w, y + h)), dst)
        cv.save(os.path.join(dbg, "preview_panel.png"))
        print("wrote debug preview to", dbg)


if __name__ == "__main__":
    main()
