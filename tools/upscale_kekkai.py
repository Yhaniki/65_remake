#!/usr/bin/env python3
r"""把 SCN0008 埃及古墓地板結界的魔法陣貼圖 (3DEFT/GENERIC/MAP_G/KEKKAI, 512x512) 重建成 2048x2048。

為什麼不用一般的 bicubic/Lanczos:
    這張圖在遊戲裡是 kikkai_3.eft 的地板結界 (SceneEftCatalog: SCN0008 -> kikkai_3, scale 40),
    整個鋪滿舞台地板, 近端在 1080p 下被拉到 ~1500px —— 512 的貼圖等於放大 3 倍, GPU 的 bilinear
    直接把 1~3px 的細線糊成寬帶。單純 Lanczos 放大不會多出任何細節 (來源就那 512 個樣本),
    GPU 本來就在做同一件事, 放大完遊戲裡看起來一樣糊。

    真正有效的是「把糊掉的東西重建回去」。這張圖 90% 的面積是規則幾何 (同心圓 / 六芒星 /
    三個圓章的框 / 放射分隔線), 那些可以用參數在任意解析度重畫成完美銳利的線; 剩下的手繪符文
    無法向量化, 只能壓縮它被拉寬的邊緣過渡帶。

管線:
    1. 底圖 = Lanczos 上採樣。低頻 (整體光暈、色階、面的漸層) 完全沿用它 —— 氛圍與配色不變。
    2. 符文層 = 對底圖做兩段 unsharp, 把被攤寬的邊緣重新收窄。
    3. 幾何層 = 用下面量到的參數重畫; 亮度取自鄰近像素的峰值, 所以線的明暗自動對齊原圖,
       不必手調。圓章圓盤內部的其他線條會被遮掉 (原畫就是圓章蓋在主環上)。
    4. 合成 = 底圖 + (銳利版 - 銳利版的模糊版) —— 只補「被抹掉的高頻」, 低頻一點都不動。
       負向 (線兩側的暗環) 乘 0.25 壓掉, 免得出現描邊感的 halo。
    5. 上色 = 用原圖自己的 亮度->RGB 曲線 (核心白熱、外暈青) 查表, 顏色不會漂掉。

    幾何參數全部量自原圖, 不是目測: 圓心用環能量網格搜尋; 半徑用徑向剖面的峰; 線寬用 FWHM;
    三角形旋轉角與圓章圓心各自擬合 —— 原畫本來就不完全對稱 (兩個三角形差 ~1 度, 三個圓章的
    圓心距差 ~2px), 硬套對稱值會和原圖錯開, 疊起來就是雙線重影。

檔案流向 (為什麼是 overlay 而不是就地改):
    assets/sdox_offline/Extracted/  是唯讀的解包原始資料, 兩條管線都從它出發 ——
        build_clean_data.ps1 -> H:\65_remake_clean\DATA   (編輯器/開發用, data_root.txt)
        package_build.ps1    -> Build\*\DATA              (打包後的 dance.exe)
    所以重建結果落在 repo 內的 art/upscaled/3DEFT/GENERIC/MAP_G, 兩個腳本各自 overlay 一次。

貼圖優先序不用動: ScreenGameplay.ResolveEftTex 本來就是 PNG 優先 (原廠 KEKKAI.BMP 留著當
fallback), 而且是 UV 全幅取樣, 換解析度不影響任何座標。

但**貼圖換大必須配一件事: mipmap**。載入端原本是 Texture2D(..., mipChain:false), 沒有 mip
時 GPU 每個螢幕像素只取一個 bilinear 樣本; 鏡頭幾乎貼地時遠端一個像素跨掉貼圖上幾十個 texel,
取樣密度遠低於訊號頻率, 就混疊成一圈圈原圖根本沒有的摩爾紋 (看起來像重影)。貼圖愈銳利愈明顯 ——
所以 512 原圖反而看不太出來, 換成這張 2048 之後才爆出來。

配套的載入端改動在 SdoExtracted.LoadTextureEft (ScreenGameplay.ResolveEftTex 呼叫):
≥1024 的 EFT 貼圖給 mip chain + trilinear + aniso; 小張的粒子圖維持原本的無 mip bilinear。
少了那一段, 這張圖在遊戲裡會比 512 原圖還糟。

依賴: pip install opencv-python pillow numpy scipy   (實際只用到 pillow/numpy/scipy)

用法:
    python tools/upscale_kekkai.py             # 產生 art/upscaled/3DEFT/GENERIC/MAP_G/KEKKAI.png
    python tools/upscale_kekkai.py --apply     # 順便同步到 clean\DATA 與 Build\*\DATA
    python tools/upscale_kekkai.py --restore   # 把上述位置還原成 Extracted 的 512px 原圖
"""
import argparse
import os
import shutil

import numpy as np
from PIL import Image, ImageDraw
from scipy.ndimage import gaussian_filter, maximum_filter

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REL = os.path.join("3DEFT", "GENERIC", "MAP_G", "KEKKAI.png")
SRC = os.path.join(REPO, "assets", "sdox_offline", "Extracted", REL)
OUT = os.path.join(REPO, "art", "upscaled", REL)
TARGETS = [                                    # --apply / --restore 會同步的位置
    os.path.join(r"H:\65_remake_clean\DATA", REL),
    os.path.join(REPO, "Build", "Dance", "DATA", REL),
    os.path.join(REPO, "Build", "Windows", "DATA", REL),
]

# ---------------------------------------------------------------- 量到的幾何 (單位: 512 座標)
S0 = 512.0
CX, CY = 255.0, 253.9                          # 圓心 (環能量網格搜尋)

RINGS = [                                      # (半徑, 線寬)  線寬 ≈ FWHM 扣掉原圖 PSF
    (202.43, 1.70),                            # 最外細圓          FWHM 2.00
    (173.00, 2.90),                            # 主環 (最亮)      FWHM 3.45
    (164.63, 1.95),                            # 環形符文帶內界    FWHM 2.30
    (81.75,  2.00),                            # 中央三圈(外) = 六芒星內切圓
    (77.50,  1.80),                            # 中央三圈(中)
    (69.75,  1.95),                            # 中央三圈(內)
]
TRIS = [(29.85, 163.60), (90.95, 163.00)]      # 六芒星的兩個三角形 (旋轉角, 外接圓半徑), 各自擬合
W_STAR = 2.00                                  # FWHM 2.40
DIVIDERS = (29.40, 149.85, 270.60)             # 外環帶的三條放射分隔線 (實測角度, 非整數)
DIV_R0, DIV_R1, W_DIV = 173.0, 202.43, 1.85    # FWHM 2.20
SEALS = [                                      # 三個圓章 (圓心x, 圓心y, 外圈r, 內圈r), 各自擬合
    (253.00, 433.90, 53.00, 45.75),
    (99.75,  166.35, 52.75, 45.75),
    (409.85, 165.65, 53.00, 46.00),
]
W_SEAL1, W_SEAL2 = 2.60, 1.50


def _polar(r, adeg):
    a = np.deg2rad(adeg)
    return (CX + r * np.cos(a), CY + r * np.sin(a))


def _blur(a, s):
    return gaussian_filter(a.astype(np.float32), s, mode="constant", truncate=3.0)


def _disk(r):
    y, x = np.mgrid[-r:r + 1, -r:r + 1]
    return (x * x + y * y) <= r * r


def _dilate(a, radius):
    return maximum_filter(a.astype(np.float32), footprint=_disk(max(1, int(round(radius)))),
                          mode="constant")


def draw_geometry(size, ss=4, lw_scale=1.0):
    """幾何線條遮罩 float32 [0,1]。ss = 超採樣倍率 (先畫大再降, 邊緣才不會有階梯)。

    lw_scale < 1 是刻意的:量到的是 FWHM(半高全寬),直接拿來當實心線寬會畫出「平頂」的線,
    比原圖那種中心亮、往兩側衰減的發光線更粗更白。畫細一點再柔化,剖面才像原本的線。"""
    S = size * ss
    k = S / S0
    img = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(img)

    def circ(dr, cx, cy, r, lw):
        rr, w = r * k, max(1, int(round(lw * lw_scale * k)))
        dr.ellipse([cx * k - rr, cy * k - rr, cx * k + rr, cy * k + rr], outline=255, width=w)

    def seg(dr, a, b, lw):
        dr.line([a[0] * k, a[1] * k, b[0] * k, b[1] * k], fill=255,
                width=max(1, int(round(lw * lw_scale * k))))

    for r, lw in RINGS:
        circ(d, CX, CY, r, lw)

    for rot, rtri in TRIS:
        p = [_polar(rtri, rot + i * 120) for i in range(3)]
        for i in range(3):
            seg(d, p[i], p[(i + 1) % 3], W_STAR)

    for adeg in DIVIDERS:
        seg(d, _polar(DIV_R0, adeg), _polar(DIV_R1, adeg), W_DIV)

    # 圓章: 先把自己的圓盤清空 (主環/符文帶在原畫裡被圓章蓋住), 再畫自己的兩圈
    mask = Image.new("L", (S, S), 0)
    dm = ImageDraw.Draw(mask)
    for sx, sy, r1, _ in SEALS:
        rr = (r1 + W_SEAL1 * 0.5) * k
        dm.ellipse([sx * k - rr, sy * k - rr, sx * k + rr, sy * k + rr], fill=255)
    img = Image.composite(Image.new("L", (S, S), 0), img, mask)
    d = ImageDraw.Draw(img)
    for sx, sy, r1, r2 in SEALS:
        circ(d, sx, sy, r1, W_SEAL1)
        circ(d, sx, sy, r2, W_SEAL2)

    return np.asarray(img.resize((size, size), Image.LANCZOS)).astype(np.float32) / 255.0


def _tint_rgb(base_rgb, base, out, lut):
    """把「補上去的亮度」用該亮度在原圖對應的顏色方向上色;底圖 RGB 原封不動。"""
    gain = out - base
    tint = lut[np.clip(out.astype(int), 0, 255)]
    tint = tint / np.maximum(tint.max(axis=-1, keepdims=True), 1e-3)
    return np.clip(base_rgb + gain[..., None] * tint, 0, 255)


def seal_disks(size):
    """三個圓章圓盤的遮罩 —— 圓章內的手繪符號不讓幾何層去動它。"""
    S = size * 2
    k = S / S0
    m = Image.new("L", (S, S), 0)
    dm = ImageDraw.Draw(m)
    for sx, sy, r1, _ in SEALS:
        rr = (r1 - 1.0) * k
        dm.ellipse([sx * k - rr, sy * k - rr, sx * k + rr, sy * k + rr], fill=255)
    return np.asarray(m.resize((size, size), Image.LANCZOS)).astype(np.float32) / 255.0


def build_lut(src_rgb):
    """原圖自己的 亮度->RGB 曲線 (核心接近白, 外暈青), 用來給重建後的亮度上色。"""
    L = src_rgb.max(axis=2)
    lut = np.zeros((256, 3), np.float32)
    cnt = np.zeros(256, np.float32)
    li = np.clip(L.astype(int), 0, 255)
    for c in range(3):
        np.add.at(lut[:, c], li.ravel(), src_rgb[..., c].ravel())
    np.add.at(cnt, li.ravel(), 1)
    have = cnt > 20
    lut[have] /= cnt[have, None]
    idx = np.arange(256)
    for c in range(3):
        lut[:, c] = np.interp(idx, idx[have], lut[have, c])    # 樣本太少的亮度階內插補齊
    lut[0] = 0
    for i in range(1, 256):
        lut[i] = np.maximum(lut[i], lut[i - 1])                # 保持單調, 免得出現色帶反轉
    return lut


def rebuild(src_path, size=2048, ss=4, amt=0.9, lw_scale=0.75, soften=0.35, dilate_r=1.5):
    """
    只增不減:低頻(光暈、色階、面的漸層)與 RGB 色彩 100% 沿用上採樣底圖,重建出來的銳利核心
    只在「比底圖亮」的地方補上去。

    為什麼要這樣:這張圖的視覺重點是「亮核心 + 厚實光暈」的發光管質感,而緊貼線條的那圈光暈
    正是靈魂。先前版本做了兩件會削掉它的事 —— 把幾何線周圍的原始亮度壓到 0.2 倍(本意是去殘影)、
    以及 unsharp 的負向 overshoot —— 結果線是變利了,光暈卻被挖掉,整體反而比 512 原圖乾癟。
    改成 max() 之後,銳化的負向自動被濾掉,不可能再挖到任何東西。
    """
    src = np.asarray(Image.open(src_path).convert("RGB")).astype(np.float32)
    lut = build_lut(src)
    scale = size / S0

    # RGB 一起上採樣:先前壓成單通道亮度再用 LUT 上色,會把原圖各處的色彩差異抹平成一條曲線
    base_rgb = np.stack([np.asarray(Image.fromarray(src[..., c], "F").resize((size, size), Image.LANCZOS))
                         for c in range(3)], -1).clip(0, 255)
    base = base_rgb.max(axis=2)

    runes = base + 1.15 * (base - _blur(base, 1.5 * scale))
    runes = runes + 0.70 * (runes - _blur(runes, 0.75 * scale))

    # 幾何線:畫細 -> 取鄰近峰值點亮 -> 柔化成中心亮、兩側衰減的剖面(而不是平頂)
    geo = draw_geometry(size, ss, lw_scale)
    geo_lit = geo * _dilate(_blur(base, 0.8 * scale), dilate_r * scale)
    geo_lit = _blur(geo_lit, soften * scale)

    ideal = np.maximum(geo_lit, runes)
    out = np.clip(base + amt * np.maximum(ideal - base, 0), 0, 255)   # 只往上加
    return Image.fromarray(_tint_rgb(base_rgb, base, out, lut).astype(np.uint8))


def copy_over(src, dst, label):
    if not os.path.exists(src):
        print("  [skip] %s — 來源不存在: %s" % (label, src))
        return
    d = os.path.dirname(dst)
    if not os.path.isdir(d):
        print("  [skip] %s — 目標資料夾不存在: %s" % (label, d))
        return
    shutil.copy2(src, dst)
    print("  [ok]   %s -> %s" % (label, dst))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=SRC, help="512px 原圖 (Extracted, 唯讀)")
    ap.add_argument("--out", default=OUT, help="overlay 輸出路徑")
    ap.add_argument("--size", type=int, default=2048, help="輸出邊長 (預設 2048)")
    ap.add_argument("--apply", action="store_true", help="同時同步到 clean\\DATA 與 Build\\*\\DATA")
    ap.add_argument("--restore", action="store_true", help="把上述位置還原成 512px 原圖")
    a = ap.parse_args()

    if a.restore:
        for t in TARGETS:
            copy_over(a.src, t, "restore 512px")
        if os.path.exists(a.out):
            os.remove(a.out)
            print("  [ok]   removed %s" % a.out)
        return

    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    img = rebuild(a.src, size=a.size)
    img.save(a.out)
    print("wrote %s  %dx%d" % (a.out, img.size[0], img.size[1]))

    if a.apply:
        for t in TARGETS:
            copy_over(a.out, t, "apply")


if __name__ == "__main__":
    main()
