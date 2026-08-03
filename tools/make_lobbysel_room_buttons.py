#!/usr/bin/env python3
r"""烘「建立房間 / 加入房間」兩顆按鈕的三態圖,風格對齊官方 LOBBYSEL 的 登入/商城 鈕。

為什麼是重烘而不是回收既有素材:
    實測過 LOBBYSEL47/48/49 是 256x40 的橫幅、50 是 32x40 —— 尺寸完全不對,當按鈕會被拉爆。
    整個 Extracted 只有 6 張 93x64 的圖,就是 LOGIN(29/30/31) 與 MALL(32/33/34) 這兩顆的三態。
    所以底板只能從它們身上拿:抹掉字、留下底板,再把新的字烘回去。

底板怎麼抹乾淨(這步是全檔最容易做壞的地方):
    底板是一個平滑的 2D 漸層(上緣一條亮反光、中央比兩側亮一點),字是「白填色 + 2px 深紫外框」。
    直接用顏色分類抹不掉外框 —— 外框 892c82 的 G/R 比值 0.32 與底板最亮處 b23c9b 的 0.34 幾乎一樣,
    分不開。改用幾何:白色好認(min(R,G,B) 高),外框一定貼著白色 → 把白色遮罩往外長 3px 就把外框
    連 AA 一起蓋住,再交給 cv2.inpaint(Telea) 從洞的邊界往裡填。漸層平滑所以填出來看不出接縫。

字怎麼烘:
    每個字先在大尺寸下算出墨水框(ink bbox),再等比縮到官方量到的格子裡 —— 這樣換字型也不會跑位:
        英文行  y=9..18(cap height 10px)、整串橫跨 x=11..81,字間距自動算到剛好撐滿
        中文行  兩字各 22x22,左字 x=22、右字 x=54(pitch 32),y=26..47
    整層在 4x 畫布上做(外框 = 遮罩 dilate 8px),最後 INTER_AREA 降回 1x —— 邊緣的 AA 才跟原圖一樣軟。
    白色與外框色是**逐狀態量測**的(hover 態的外框比 normal 亮),不是寫死一組。

用字:官方的排版是「上排小英文 + 下排兩個大中文」。兩個中文字是唯一真的會被讀的資訊,
    所以中文放辨識度最高的兩個字(開房 / 進房),英文只是裝飾。
    ⚠️ 不要改成四個中文字塞一行:93px 寬只能給到 19px 字身,會比隔壁的 商城 擠很多,一眼就看得出不是同一套。

輸出:art/generated/UI/LOBBYSEL/LOBBYSEL200..205.PNG + .AN
    200/201/202 = 建立房間 normal/hover/pushed
    203/204/205 = 加入房間 normal/hover/pushed
    .AN 是官方那種「單行一個 png 檔名」的格式,載入端零新程式(LobbySelArt.An("LobbySel200") 直接吃)。

🔴 art/generated 必須接進**兩支**打包腳本(package_build.ps1 與 build_clean_data.ps1),
   只接一支的結果是「編輯器裡有圖、打包版沒圖」—— 那種 bug 要等到出貨才會發現。

依賴: pip install opencv-python pillow numpy

用法:
    python tools/make_lobbysel_room_buttons.py                 # 產生 art/generated/UI/LOBBYSEL
    python tools/make_lobbysel_room_buttons.py --apply         # 順便同步到 clean\DATA(免得為了看效果重跑 build_clean_data)
    python tools/make_lobbysel_room_buttons.py --preview out.png   # 烘出來的與官方鈕併排放大,給眼睛看
"""
import argparse
import os
import shutil
import subprocess
import sys

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_REL = os.path.join("UI", "LOBBYSEL")
OVERLAY = os.path.join(REPO, "art", "generated", OUT_REL)

# ---- 官方量到的版位(LOBBYSEL29,93x64)----
PLATE_W, PLATE_H = 93, 64
EN_Y0, EN_Y1 = 9, 18            # 英文行的墨水上下緣(含)
EN_X0, EN_X1 = 23, 70           # 英文行整串的左右緣(含)—— LOGIN 是 23..70、MALL 是 24..72
CJK_Y0, CJK_Y1 = 26, 47         # 中文行的墨水上下緣(含)
CJK_X0, CJK_PITCH, CJK_BOX = 22, 32, 22   # 左字左緣 / 字距 / 字身方格
SS = 4                          # 文字層的超取樣倍率

# 字型。英文那顆是方頭斜體的科技感字(Eurostile/Square721 一路),Windows 沒有;
# Franklin Gothic Medium Italic 是庫存字型裡字腔最方的斜體,再補一點傾斜去貼原圖的角度。
FONT_EN = r"C:\Windows\Fonts\framdit.ttf"
FONT_EN_SHEAR = 0.10            # 額外傾斜(0 = 只用字型自己的斜度)
# 中文是粗黑體。微軟正黑體 Bold 的字腔最接近官方那種方頭粗黑。
FONT_CJK = r"C:\Windows\Fonts\msjhbd.ttc"

# 加粗量(1x 像素)。官方的筆畫比庫存字型的 Bold 還粗一階 —— Windows 沒有更粗的正黑/黑體,
# 所以用形態學把筆畫長胖。數值是拿 --calib(同管線重烘 LOGIN/登入 與官方併排)調出來的。
BOLD_EN, BOLD_CJK = 0.55, 0.7
# 英文行是**齊寬**的:量官方 LOGIN(5 字)寬 48px、MALL(4 字)寬 49px —— 字少就把字距拉開,
# 兩顆一樣寬。所以字距是算出來的(撐滿 EN_X0..EN_X1),不是固定值。
# 字身佔比:LOGIN 墨水 36/48 = 0.75、MALL 30/49 = 0.61 → 目標 0.70 附近。
EN_INK_RATIO = 0.70
# 英文字身橫向加寬。官方那顆字腔是方的(O 幾乎是圓角方形),庫存的 Franklin Gothic 比它窄;
# 橫向拉寬同時把字腔變方,兩件事一起解決。1.0 = 不動;實際值由 EN_INK_RATIO 反推。
EN_WIDEN = 1.30
# 外框厚度(1x 像素)。量官方是 2px:白色往外兩圈都還是 #892c82。
OUTLINE_PX = 2.0
# 落影:官方白字的下緣/右緣比上緣暗一截,那是一層偏移的暗影而不是均勻外框。
SHADOW_OFF = (1.0, 1.5)     # (dx, dy) 1x 像素
SHADOW_MUL = 0.55           # 影子色 = 外框色 × 這個係數

BUTTONS = [
    # (輸出編號起點, 英文, 中文, 說明)
    (200, "OPEN", "開房", "建立房間"),
    (203, "JOIN", "加入", "加入房間"),
]
SRC_STATES = ["LOBBYSEL29", "LOBBYSEL30", "LOBBYSEL31"]   # normal / hover / pushed


# ---- 素材來源 ----

def data_root():
    """clean\\DATA(data_root.txt 指的地方);沒有就 None。"""
    p = os.path.join(REPO, "data_root.txt")
    if not os.path.isfile(p):
        return None
    root = open(p, encoding="utf-8-sig").read().strip()
    return root if root and os.path.isdir(root) else None


def resolve_src_dir():
    """找 LOBBYSEL 原圖。worktree 裡沒有 assets/(那棵樹只在主 worktree),所以要往上找。"""
    cands = [os.path.join(REPO, "assets", "sdox_offline", "Extracted", OUT_REL)]
    # git worktree:--git-common-dir 會指回主 repo 的 .git,它的上一層就是主 worktree
    try:
        common = subprocess.check_output(["git", "rev-parse", "--git-common-dir"],
                                         cwd=REPO, text=True).strip()
        main_wt = os.path.dirname(os.path.abspath(os.path.join(REPO, common)))
        cands.append(os.path.join(main_wt, "assets", "sdox_offline", "Extracted", OUT_REL))
    except Exception:
        pass
    # 最後才吃 clean\DATA —— 它是 Extracted 的忠實副本,這裡只讀不寫。
    root = data_root()
    if root:
        cands.append(os.path.join(root, OUT_REL))
    for c in cands:
        if os.path.isfile(os.path.join(c, SRC_STATES[0] + ".PNG")):
            return c
    sys.exit("找不到 LOBBYSEL 原圖,找過:\n  " + "\n  ".join(cands))


# ---- 抹字 ----

def white_mask(rgba):
    """白色字身。上緣那條反光也是淡色的,所以只在字的行區間裡認。"""
    mn = rgba[..., :3].min(2)
    m = (mn > 170) & (rgba[..., 3] > 150)
    m[:EN_Y0] = False
    m[CJK_Y1 + 1:] = False
    return m.astype(np.uint8)


def outline_colour(rgba, white):
    """量這一態的外框色:白色字身往外一圈裡最常出現的顏色。"""
    ring = cv2.dilate(white, np.ones((3, 3), np.uint8)) & ~white
    px = rgba[..., :3][ring.astype(bool)]
    if len(px) == 0:
        return (137, 44, 130)
    # 取最暗的四分之一裡的中位數 —— 一圈裡也混著白↔框之間的 AA,直接取眾數會偏亮
    lum = px.sum(1)
    dark = px[lum <= np.percentile(lum, 25)]
    return tuple(int(v) for v in np.median(dark, 0))


def strip_text(rgba):
    """回傳「只有底板」的 RGBA。"""
    white = white_mask(rgba)
    hole = cv2.dilate(white, np.ones((3, 3), np.uint8), iterations=3)
    # 護住外圈:圓角與邊框那圈絕不能被 inpaint 動到(那是按鈕的形狀本身)
    guard = np.zeros_like(hole)
    guard[EN_Y0 - 3:CJK_Y1 + 4, 4:PLATE_W - 4] = 1
    hole &= guard
    out = rgba.copy()
    for ch in range(4):
        out[..., ch] = cv2.inpaint(rgba[..., ch].astype(np.uint8), hole, 5, cv2.INPAINT_TELEA)
    return out, outline_colour(rgba, white)


# ---- 排字 ----

def glyph_alpha(font_path, ch, px=240, shear=0.0):
    """把一個字畫成「墨水框剛好貼齊」的 alpha 圖(灰階 numpy)。

    為什麼不直接用字型 metrics 定位:字型的 ascent/descent 各家不一,換字型就跑位。
    改用實際墨水框 → 縮放與定位只跟「官方量到的格子」有關,與字型無關。
    """
    font = ImageFont.truetype(font_path, px)
    pad = px // 2
    img = Image.new("L", (px * 3 + pad * 2, px * 2 + pad * 2), 0)
    ImageDraw.Draw(img).text((pad, pad), ch, fill=255, font=font)
    if shear:
        w, h = img.size
        img = img.transform((w + int(abs(shear) * h), h), Image.AFFINE,
                            (1, shear, -shear * h if shear > 0 else 0, 0, 1, 0),
                            resample=Image.BICUBIC)
    a = np.array(img)
    ys, xs = np.nonzero(a > 8)
    if len(ys) == 0:
        return np.zeros((1, 1), np.uint8)
    return a[ys.min():ys.max() + 1, xs.min():xs.max() + 1]


def fit(a, h=None, w=None, widen=1.0):
    """等比縮放到指定高(或寬);widen 再額外橫向拉寬。"""
    ah, aw = a.shape
    s = (h / ah) if h else (w / aw)
    nh, nw = max(1, int(round(ah * s))), max(1, int(round(aw * s * widen)))
    return cv2.resize(a, (nw, nh), interpolation=cv2.INTER_AREA)


def embolden(a, px):
    """把筆畫長胖 px 個 1x 像素(在 SS 畫布上做,所以半徑要乘 SS)。"""
    r = int(round(px * SS))
    if r < 1:
        return a
    return cv2.dilate(a, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * r + 1,) * 2))


def layout_text(en, cjk):
    """在 SS 倍的畫布上排好英文行與中文行,回傳 alpha 遮罩(0..255)。"""
    canvas = np.zeros((PLATE_H * SS, PLATE_W * SS), np.uint8)

    def paste(a, x, y):
        h, w = a.shape
        y0, x0 = int(round(y)), int(round(x))
        sub = canvas[y0:y0 + h, x0:x0 + w]
        np.maximum(sub, a[:sub.shape[0], :sub.shape[1]], out=sub)

    # ---- 英文行:每個字母等比縮到 cap height + 橫向加寬,字距算到剛好撐滿官方寬度 ----
    en_h = (EN_Y1 - EN_Y0 + 1) * SS
    inner_h = en_h - int(round(BOLD_EN * SS)) * 2        # 先留加粗要吃掉的高度
    span = (EN_X1 - EN_X0 + 1) * SS
    raw = [glyph_alpha(FONT_EN, c, shear=FONT_EN_SHEAR) for c in en]
    # 先算「不加寬」時的墨水總寬,再反推要多寬才會到目標佔比 —— 這樣換字型也不用重調。
    base = [fit(g, h=inner_h) for g in raw]
    widen = EN_WIDEN if EN_WIDEN else 1.0
    natural = sum(g.shape[1] for g in base)
    if natural > 0:
        widen = min(1.9, max(0.7, span * EN_INK_RATIO / natural))
    gl = [embolden(fit(g, h=inner_h, widen=widen), BOLD_EN) for g in raw]
    ink = sum(g.shape[1] for g in gl)
    track = (span - ink) / max(1, len(gl) - 1)
    x = EN_X0 * SS
    for g in gl:
        paste(g, x, EN_Y0 * SS)
        x += g.shape[1] + track
    layout_text.stats = (span / SS, ink / SS, track / SS, widen)

    # ---- 中文行:兩字各自塞進 22x22 的方格(官方就是等寬等距)----
    box = CJK_BOX * SS
    for i, c in enumerate(cjk):
        g = glyph_alpha(FONT_CJK, c)
        # 先留出加粗要吃掉的空間,免得長胖之後超出方格
        inner = box - int(round(BOLD_CJK * SS)) * 2
        g = fit(g, h=inner) if g.shape[0] >= g.shape[1] else fit(g, w=inner)
        g = embolden(g, BOLD_CJK)
        gh, gw = g.shape
        cx = (CJK_X0 + i * CJK_PITCH) * SS + (box - gw) / 2
        cy = CJK_Y0 * SS + (box - gh) / 2
        paste(g, cx, cy)
    return canvas


def bake(plate, outline, en, cjk):
    """把字烘到底板上。外框/落影在 SS 畫布上用 dilate 做,最後一起降取樣 → AA 與原圖同軟。

    疊法(由下往上):落影 → 外框 → 白色字身。三層都在同一張 SS 畫布上算,
    只降取樣一次 —— 分層降取樣會讓每層各自 AA,邊緣就會有一圈半透明的髒邊。
    """
    core = layout_text(en, cjk)
    ring = embolden(core, OUTLINE_PX)
    shadow = np.roll(np.roll(ring, int(round(SHADOW_OFF[1] * SS)), 0),
                     int(round(SHADOW_OFF[0] * SS)), 1)

    h, w = core.shape
    oc = np.array(outline, np.float32)
    layer = np.zeros((h, w, 4), np.float32)
    layer[..., :3] = oc * SHADOW_MUL
    layer[..., 3] = shadow
    for mask, colour in ((ring, oc), (core, np.float32([254, 254, 254]))):
        a = (mask / 255.0)[..., None]
        layer[..., :3] = layer[..., :3] * (1 - a) + colour * a
        layer[..., 3] = np.maximum(layer[..., 3], mask)

    small = cv2.resize(layer, (PLATE_W, PLATE_H), interpolation=cv2.INTER_AREA)
    out = plate.astype(np.float32).copy()
    ta = (small[..., 3] / 255.0)[..., None]
    out[..., :3] = out[..., :3] * (1 - ta) + small[..., :3] * ta
    # alpha 只能變不透明,不能把底板的圓角吃掉
    out[..., 3] = np.maximum(out[..., 3], small[..., 3])
    return np.clip(out, 0, 255).astype(np.uint8)


# ---- 主流程 ----

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="順便同步到 clean\\DATA")
    ap.add_argument("--preview", metavar="PNG", help="輸出「官方 vs 烘出來」的放大對照圖")
    ap.add_argument("--calib", metavar="PNG",
                    help="校準用:拿同一套管線重烘官方的 LOGIN/登入 與 MALL/商城,與原圖上下併排。"
                         "字重/字距/版位調對了的話,烘出來的應該幾乎疊得上原圖。")
    ap.add_argument("--bold-en", type=float, default=None, help="覆寫英文加粗量(1x px)")
    ap.add_argument("--bold-cjk", type=float, default=None, help="覆寫中文加粗量(1x px)")
    ap.add_argument("--font-en", default=None, help="覆寫英文字型")
    ap.add_argument("--font-cjk", default=None, help="覆寫中文字型")
    ap.add_argument("--shear", type=float, default=None, help="覆寫英文額外傾斜")
    args = ap.parse_args()

    global BOLD_EN, BOLD_CJK, FONT_EN, FONT_CJK, FONT_EN_SHEAR
    if args.bold_en is not None: BOLD_EN = args.bold_en
    if args.bold_cjk is not None: BOLD_CJK = args.bold_cjk
    if args.font_en: FONT_EN = args.font_en
    if args.font_cjk: FONT_CJK = args.font_cjk
    if args.shear is not None: FONT_EN_SHEAR = args.shear

    src = resolve_src_dir()
    print("[btn] 原圖來源:", src)
    os.makedirs(OVERLAY, exist_ok=True)

    plates, outlines = [], []
    for name in SRC_STATES:
        rgba = np.array(Image.open(os.path.join(src, name + ".PNG")).convert("RGBA"))
        p, oc = strip_text(rgba)
        plates.append(p)
        outlines.append(oc)
        print("[btn] %s 底板抹好,外框色 #%02x%02x%02x" % ((name,) + oc))

    if args.calib:
        # 同一套管線重烘官方那兩顆的字 → 與原圖上下併排。差異一眼看得出來(字重、字距、版位)。
        pairs = [("LOGIN", "登入", 0), ("MALL", "商城", 3)]
        S = 6
        cw, chh = PLATE_W * S + 8, PLATE_H * S + 8
        sheet = Image.new("RGBA", (cw * 2, chh * 2), (32, 32, 40, 255))
        for col, (en, cjk, off) in enumerate(pairs):
            orig = Image.open(os.path.join(src, "LOBBYSEL%d.PNG" % (29 + off))).convert("RGBA")
            mine = Image.fromarray(bake(plates[0], outlines[0], en, cjk))
            sheet.alpha_composite(orig.resize((PLATE_W * S, PLATE_H * S), Image.NEAREST), (col * cw + 4, 4))
            sheet.alpha_composite(mine.resize((PLATE_W * S, PLATE_H * S), Image.NEAREST), (col * cw + 4, chh + 4))
        sheet.convert("RGB").save(args.calib)
        sp, ink, tr, wd = layout_text.stats
        print("[btn] 校準圖(上=官方 下=烘的):", args.calib)
        print("[btn]   bold_en=%s bold_cjk=%s outline=%s → 英文行 span=%.0f ink=%.1f track=%.1f widen=%.2f"
              % (BOLD_EN, BOLD_CJK, OUTLINE_PX, sp, ink, tr, wd))
        return

    made = []
    for base, en, cjk, label in BUTTONS:
        for i in range(3):
            img = bake(plates[i], outlines[i], en, cjk)
            num = base + i
            png = os.path.join(OVERLAY, "LOBBYSEL%d.PNG" % num)
            Image.fromarray(img).save(png)
            # .AN 用官方的大小寫慣例(LobbySel29.png),檔案本體全大寫 —— 與 Extracted 一致
            with open(os.path.join(OVERLAY, "LOBBYSEL%d.AN" % num), "w", encoding="ascii") as f:
                f.write("LobbySel%d.png" % num)
            made.append((num, img))
        print("[btn] LOBBYSEL%d..%d = %s (%s / %s)" % (base, base + 2, label, en, cjk))

    if args.apply:
        root = data_root()
        if not root:
            sys.exit("--apply 需要 data_root.txt")
        dst = os.path.join(root, OUT_REL)
        for f in sorted(os.listdir(OVERLAY)):
            shutil.copy2(os.path.join(OVERLAY, f), os.path.join(dst, f))
        print("[btn] 已同步到", dst)

    if args.preview:
        official = [np.array(Image.open(os.path.join(src, n + ".PNG")).convert("RGBA")) for n in SRC_STATES]
        official += [np.array(Image.open(os.path.join(src, "LOBBYSEL%d.PNG" % n)).convert("RGBA"))
                     for n in (32, 33, 34)]
        rows = [official, [img for _, img in made[:3]], [img for _, img in made[3:]]]
        S = 4
        cw, ch = PLATE_W * S + 8, PLATE_H * S + 8
        sheet = Image.new("RGBA", (cw * 6, ch * len(rows)), (32, 32, 40, 255))
        for r, row in enumerate(rows):
            for c, img in enumerate(row):
                big = Image.fromarray(img).resize((PLATE_W * S, PLATE_H * S), Image.NEAREST)
                sheet.alpha_composite(big, (c * cw + 4, r * ch + 4))
        sheet.convert("RGB").save(args.preview)
        print("[btn] 對照圖:", args.preview)


if __name__ == "__main__":
    main()
