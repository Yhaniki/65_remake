#!/usr/bin/env python3
r"""把 UI/PLAYINGEXP 的表情 cut-in (64x64) 放大 3 倍成 192x192, 產出成 overlay。

為什麼不用一般的 bicubic/Lanczos:
    這批圖是 binary alpha (只有 0/255) 的手繪 pixel art, 透明區塞白色 matte (255,255,255,0)。
    一般插值只會把 64px 的階梯邊「糊開」, 遊戲裡看起來一樣模糊, 沒有真正變清楚。
    hq3x 是專為 pixel art 設計的邊緣偵測放大, 會把階梯邊重建成平滑曲線 -> 遊戲裡才真的變利。

管線 (每張圖):
    1. alpha-bleed: 把不透明色往透明區擴散, 蓋掉白 matte。
       必做 —— 否則放大時白色會被混進輪廓, 就是那條熟悉的白邊 halo。
    2. hq3x   RGB 與 alpha 各跑一次 (alpha 當灰階跑) -> 192x192
    3. hq2x   再放大一次 -> 384x384, 然後 INTER_AREA 降回 192
       這步等於 6x 超取樣: hq3x 單跑的輸出仍卡在 3x 網格上有細階梯, 降取樣後邊緣才乾淨。
    4. 透明區 (alpha==0) 的 RGB 補回白色, 與原始素材的慣例一致 (遊戲端 bleed:true 會自己再處理一次)。

檔案流向 (為什麼是 overlay 而不是就地改):
    assets/sdox_offline/Extracted/  是唯讀的解包原始資料, 兩條管線都從它出發 ——
        build_clean_data.ps1 -> H:\65_remake_clean\DATA   (編輯器/開發用, data_root.txt)
        package_build.ps1    -> Build\*\DATA              (打包後的 dance.exe)
    所以放大結果落在 repo 內的 art/upscaled/UI/PLAYINGEXP, 兩個腳本各自 overlay 一次。
    直接改 Extracted 的話等於污染原始資料; 只改 clean\DATA 的話下次重跑 build_clean_data 就被蓋回 64px。

遊戲端顯示大小不變: ScreenGameplay.LoadEmojiSeq 走 SdoExtracted.LoadImageAtDesignWidth(..., 64),
pixelsPerUnit = tex.width / 64, 所以 192px 的圖世界尺寸與 64px 時完全相同 (EmojiUpscaleTests 有守)。

依賴: pip install hqx opencv-python pillow numpy
    (hqx 還在 import PIL.PyAccess, Pillow>=11 已移除, 下面用 shim 補掉)

用法:
    python tools/upscale_playingexp.py            # 產生 art/upscaled/UI/PLAYINGEXP
    python tools/upscale_playingexp.py --apply    # 順便同步到 clean\DATA (免得為了看效果重跑 build_clean_data)
    python tools/upscale_playingexp.py --restore  # 把 clean\DATA 的表情圖還原成 Extracted 的 64px 原圖
"""
import argparse
import os
import re
import shutil
import sys
import types

import PIL  # noqa: E402

# hqx 1.x 仍 import PIL.PyAccess (Pillow 12 拿掉了) —— 只是 import 期的型別註記, 塞個空殼即可。
if not hasattr(PIL, "PyAccess"):
    _shim = types.ModuleType("PIL.PyAccess")
    _shim.PyAccess = object
    sys.modules["PIL.PyAccess"] = _shim
    setattr(PIL, "PyAccess", _shim)

import cv2  # noqa: E402
import hqx  # noqa: E402
import numpy as np  # noqa: E402
from PIL import Image  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO, "assets", "sdox_offline", "Extracted", "UI", "PLAYINGEXP")
OVERLAY = os.path.join(REPO, "art", "upscaled", "UI", "PLAYINGEXP")
CLEAN = r"H:\65_remake_clean\DATA\UI\PLAYINGEXP"

# 只碰 ScreenGameplay.LoadEmojiSeq 讀的 9 個序列。同一個資料夾還住著 COMBO*/其他 90x90 的圖,
# 那些走別的載入路徑 (ppu 1), 放大它們會直接讓畫面上的東西變 3 倍大。
EMOJI_RE = re.compile(r"^(GTH|HE|HH|JRKL|JS|KJ|SHSH|H|Y)\d{3}\.PNG$", re.IGNORECASE)
DESIGN_PX = 64
SCALE = 3


def bleed_rgb(rgb, alpha, rounds=8):
    """把不透明像素的顏色一圈圈往透明區擴散, 直到填滿 (或跑滿 rounds)。alpha 不動。"""
    rgb = rgb.astype(np.float32).copy()
    known = (alpha > 0).astype(np.float32)
    if known.max() == 0:
        return rgb.astype(np.uint8)
    kernel = np.ones((3, 3), np.float32)
    for _ in range(rounds):
        if known.min() > 0:
            break
        wsum = cv2.filter2D(known, -1, kernel, borderType=cv2.BORDER_REPLICATE)
        csum = cv2.filter2D(rgb * known[..., None], -1, kernel, borderType=cv2.BORDER_REPLICATE)
        fill = (wsum > 0) & (known == 0)
        avg = csum / np.maximum(wsum, 1e-6)[..., None]
        rgb[fill] = avg[fill]
        known[fill] = 1.0
    return np.clip(rgb, 0, 255).astype(np.uint8)


def _hq(rgb, fn):
    return np.array(fn(Image.fromarray(rgb, "RGB")).convert("RGB"))


def upscale_rgba(rgba, scale=SCALE):
    """64x64 RGBA -> (64*scale)^2 RGBA, hq3x + hq2x 超取樣。"""
    h, w = rgba.shape[:2]
    rgb, alpha = rgba[..., :3], rgba[..., 3]
    target = (w * scale, h * scale)

    bled = bleed_rgb(rgb, alpha)
    gray = np.dstack([alpha, alpha, alpha])

    # hq3x -> 3x, 再 hq2x -> 6x, 最後 INTER_AREA 降到 3x (= 2x 超取樣的抗鋸齒邊)
    big_rgb = _hq(_hq(bled, hqx.hq3x), hqx.hq2x)
    big_a = _hq(_hq(gray, hqx.hq3x), hqx.hq2x)[..., 0]

    out_rgb = cv2.resize(big_rgb, target, interpolation=cv2.INTER_AREA)
    out_a = cv2.resize(big_a, target, interpolation=cv2.INTER_AREA)

    # 全透明的地方寫回白 matte, 跟原始素材一致 (可見邊緣的 RGB 維持 bleed 後的物件色, 不會有白邊)
    out_rgb[out_a == 0] = 255
    return np.dstack([out_rgb, out_a]).astype(np.uint8)


def emoji_frames(folder):
    return sorted(n for n in os.listdir(folder) if EMOJI_RE.match(n))


def build_overlay(src, out):
    names = emoji_frames(src)
    if not names:
        print(f"[!] {src} 裡沒有表情序列 PNG")
        return 0
    os.makedirs(out, exist_ok=True)
    done = skipped = 0
    for n in names:
        rgba = np.array(Image.open(os.path.join(src, n)).convert("RGBA"))
        h, w = rgba.shape[:2]
        if (w, h) != (DESIGN_PX, DESIGN_PX):
            print(f"[skip] {n}: {w}x{h} (只處理 {DESIGN_PX}x{DESIGN_PX} 原圖)")
            skipped += 1
            continue
        Image.fromarray(upscale_rgba(rgba), "RGBA").save(os.path.join(out, n))
        done += 1
    px = DESIGN_PX * SCALE
    print(f"[ok] {done} 張 -> {px}x{px} @ {out}" + (f" (跳過 {skipped})" if skipped else ""))
    return done


def copy_over(src, dst, label):
    if not os.path.isdir(dst):
        print(f"[!] 目標不存在, 略過 {label}: {dst}")
        return 0
    n = 0
    for f in emoji_frames(src):
        shutil.copy2(os.path.join(src, f), os.path.join(dst, f))
        n += 1
    print(f"[ok] {label}: 覆蓋 {n} 張 -> {dst}")
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=SRC, help="64px 原圖來源 (唯讀)")
    ap.add_argument("--out", default=OVERLAY, help="放大後的 overlay 目錄")
    ap.add_argument("--apply", action="store_true", help="同時把 overlay 覆蓋到 clean\\DATA")
    ap.add_argument("--restore", action="store_true", help="把 clean\\DATA 還原成 --src 的 64px 原圖")
    ap.add_argument("--clean", default=CLEAN, help="clean DATA 的 PLAYINGEXP 路徑")
    a = ap.parse_args()

    if a.restore:
        copy_over(a.src, a.clean, "restore 64px")
        return 0

    if not build_overlay(a.src, a.out):
        return 1
    if a.apply:
        copy_over(a.out, a.clean, "apply -> clean DATA")
    return 0


if __name__ == "__main__":
    sys.exit(main())
