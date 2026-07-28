#!/usr/bin/env python3
r"""烘房間頭貼的狀態徽章:「缺歌」與「遊戲中」。

風格從哪裡來(這是整個決定的關鍵):
    這兩張徽章的視覺兄弟是官方的**房主徽章** MASTER.AN → b06..b09.png ——
    它就掛在同一個頭貼格的下緣(RoomScreen 的 masterX / y=102)。量它:
        100x30 畫布、白色字身 #f7f7f7、深灰外框 #313131(2px)、外圈一層 4px 的深色柔光,
        字是**方頭粗斜體的英文** "HOST"(不是中文!),字身高 20px、4 個字橫跨 74px。
    所以新徽章一律走同一套:英文、粗斜體、白字 + 深灰框 + 柔光。
    烘中文反而會變成「明顯不是同一套素材」——官方在這個位置就是用英文。

用字(對齊 osu 的多人房狀態顯示 StateDisplay):
    osu 對同一件事寫的是 "no map" 與 "playing"。這裡的單位是歌不是圖,所以:
        缺歌   → NO SONG
        遊戲中 → PLAYING
    ⚠️ 要改成中文的話改 LABELS 就好(FONT_CJK 已經備著)—— 但先看一眼 1x 的效果:
       75px 寬的格子塞三個中文字會縮到看不清,那也是原本計畫只留兩個字的原因。

尺寸為什麼不是 100x30:
    要維持 HOST 的字身高(20px),7 個字會佔到 130px 寬 —— 而座位的間距只有 121px,
    會壓到隔壁那格。所以字身降到 14px、畫布 116x26,剛好塞得進一格又不碰鄰居。
    (另一個方向是縮短用字,但 4 個字母之內沒有能讓人看懂「缺這首歌」的英文。)

上傳/下載的進度**不烘圖**:
    烘死的圖帶不了動態數字,而使用者要的就是「頭貼下方一條跑條」——
    那是執行期畫的兩個矩形(RoomScreen 的 _slotBar),不需要素材。

輸出: art/generated/UI/ROOM/{MISSING,PLAYING}.PNG + .AN
    .AN 用官方那種「單行一個 png 檔名 + 裁切框」的格式 → 載入端零新程式。

🔴 art/generated 必須接進**兩支**打包腳本(package_build.ps1 與 build_clean_data.ps1),
   只接一支的結果是「編輯器裡有圖、打包版沒圖」。

依賴: pip install opencv-python pillow numpy

用法:
    python tools/make_room_state_badges.py
    python tools/make_room_state_badges.py --apply           # 順便同步到 clean\DATA 方便看效果
    python tools/make_room_state_badges.py --preview out.png # 與官方 HOST 徽章併排放大,給眼睛看
"""
import argparse
import os
import shutil
import sys

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_REL = os.path.join("UI", "ROOM")
OVERLAY = os.path.join(REPO, "art", "generated", OUT_REL)

# ---- 從官方 b06.png 量到的規格 ----
PLATE_W, PLATE_H = 116, 26      # 我們的畫布(見檔頭:為什麼不是 100x30)
INK_H = 20                      # 含外框的字身總高(官方 HOST 是 22/30，這裡 20/26 同比例)
INK_MARGIN_X = 4                # 左右留白
OUTLINE_PX = 2                  # 外框厚度(官方量到 2px)
GLOW_PX = 4                     # 外圈柔光半徑(官方量到 4px 的部分透明深色)
GLOW_ALPHA = 0.55               # 柔光最濃處的不透明度
FILL_RGB = (247, 247, 247)      # 官方白 #f7f7f7
OUTLINE_RGB = (49, 49, 49)      # 官方深灰 #313131
GLOW_RGB = (24, 24, 24)
SS = 6                          # 超取樣倍率(同 upscale_playingexp.py 的 6x 慣例)

# 官方那顆是方頭斜體的科技感字(Eurostile/Square721 一路)，Windows 沒有;
# Franklin Gothic Medium Italic 是庫存字型裡字腔最方的斜體 —— 與 make_lobbysel_room_buttons.py 同一個選擇。
FONT_EN = r"C:\Windows\Fonts\framdit.ttf"
FONT_EN_SHEAR = 0.06            # 額外傾斜，貼原圖的角度
FONT_CJK = r"C:\Windows\Fonts\msjhbd.ttc"   # 要改中文時用(微軟正黑 Bold)
BOLD = 0.5                      # 加粗量(1x 像素)。官方筆畫比庫存 Bold 粗一階。

# 要烘哪幾張。key = 輸出檔名(對齊官方 CLOSE/MASTER/TEAM 的短名慣例)
LABELS = [
    ("MISSING", "NO SONG"),     # 缺這首歌(osu: "no map")
    ("PLAYING", "PLAYING"),     # 正在這一場裡(osu: "playing")
]


def load_font(path, px):
    try:
        return ImageFont.truetype(path, px)
    except OSError:
        sys.exit("找不到字型: %s" % path)


def text_mask(label, font_path, target_h, target_w):
    """把一串字烘成 SS 倍大小的 0..255 遮罩，等比縮到剛好塞進 (target_w, target_h)。"""
    # 先在一個大尺寸下畫，量墨水框，再算縮放 —— 這樣換字型也不會跑位。
    probe_px = 200
    font = load_font(font_path, probe_px)
    pad = probe_px
    img = Image.new("L", (probe_px * (len(label) + 2), probe_px * 2), 0)
    ImageDraw.Draw(img).text((pad // 2, pad // 4), label, font=font, fill=255)
    a = np.array(img)

    # 額外傾斜(字型自己的斜度不夠時)
    if FONT_EN_SHEAR > 0:
        h, w = a.shape
        m = np.float32([[1, -FONT_EN_SHEAR, FONT_EN_SHEAR * h], [0, 1, 0]])
        a = cv2.warpAffine(a, m, (w, h), flags=cv2.INTER_LINEAR, borderValue=0)

    ys, xs = np.where(a > 8)
    if len(ys) == 0:
        sys.exit("字烘不出來: %s" % label)
    a = a[ys.min():ys.max() + 1, xs.min():xs.max() + 1]

    # 縮到「含外框之後剛好是 target」的大小:外框會讓字往外長 OUTLINE_PX，所以字身要先扣掉。
    body_h = (target_h - 2 * OUTLINE_PX) * SS
    body_w = (target_w - 2 * OUTLINE_PX) * SS
    scale = min(body_h / a.shape[0], body_w / a.shape[1])
    new_w = max(1, int(round(a.shape[1] * scale)))
    new_h = max(1, int(round(a.shape[0] * scale)))
    a = cv2.resize(a, (new_w, new_h), interpolation=cv2.INTER_AREA)

    # 加粗(官方筆畫比庫存字型的 Bold 粗一階)
    k = max(1, int(round(BOLD * SS)))
    if k > 1:
        a = cv2.dilate(a, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    return a


def bake(label):
    W, H = PLATE_W * SS, PLATE_H * SS
    body = text_mask(label, FONT_EN, INK_H, PLATE_W - 2 * INK_MARGIN_X)

    # 置中放到大畫布上
    canvas = np.zeros((H, W), np.uint8)
    y0 = (H - body.shape[0]) // 2
    x0 = (W - body.shape[1]) // 2
    canvas[y0:y0 + body.shape[0], x0:x0 + body.shape[1]] = body

    def grow(mask, px):
        k = max(1, int(round(px * SS)) | 1)
        return cv2.dilate(mask, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))

    outline = grow(canvas, OUTLINE_PX * 2)          # dilate 的 kernel 是直徑 → 半徑要 x2

    # 柔光**只 blur、不 dilate**。先 dilate 再 blur 的話,在這麼窄的畫布上四周會長到彼此連起來 ——
    # 結果是整張圖蒙上一個深色方塊,而不是貼著字外緣的一圈光暈(第一版就是這樣,一眼就看出來)。
    glow = cv2.GaussianBlur(outline.astype(np.float32), (0, 0), GLOW_PX * SS * 0.5)
    glow = np.clip(glow / 255.0 * 1.6, 0, 1) * GLOW_ALPHA   # x1.6 補回 blur 掉的濃度

    fill_a = canvas.astype(np.float32) / 255.0
    out_a = outline.astype(np.float32) / 255.0

    # 由下往上疊:柔光 → 外框 → 字身。每層都用自己的 alpha 做 premultiplied 合成，
    # 這樣邊緣的 AA 不會出現深色鑲邊(直接覆蓋的話透明區的 RGB 會滲進來)。
    rgb = np.zeros((H, W, 3), np.float32)
    alpha = np.zeros((H, W), np.float32)
    for layer_rgb, layer_a in ((GLOW_RGB, glow), (OUTLINE_RGB, out_a), (FILL_RGB, fill_a)):
        la = layer_a[..., None]
        rgb = np.array(layer_rgb, np.float32) * la + rgb * (1 - la)
        alpha = layer_a + alpha * (1 - layer_a)

    # 降回 1x。INTER_AREA = 盒式平均 = 真正的超取樣(同 upscale_playingexp.py 第 3 步)。
    small_rgb = cv2.resize(rgb, (PLATE_W, PLATE_H), interpolation=cv2.INTER_AREA)
    small_a = cv2.resize(alpha, (PLATE_W, PLATE_H), interpolation=cv2.INTER_AREA)

    # 透明區的 RGB 補白 —— 官方素材就是這樣(b06 的四角是 255,255,255,0)。
    # 不補的話某些取樣器會把黑色滲進半透明邊緣 → 白字外面多一圈灰。
    a3 = np.clip(small_a, 0, 1)[..., None]
    rgb_out = small_rgb * a3 + 255.0 * (1 - a3)

    out = np.dstack([np.clip(rgb_out, 0, 255), np.clip(small_a * 255.0, 0, 255)]).astype(np.uint8)
    return out


def write(name, bgra_like):
    os.makedirs(OVERLAY, exist_ok=True)
    png = os.path.join(OVERLAY, name + ".PNG")
    Image.fromarray(bgra_like, "RGBA").save(png)
    with open(os.path.join(OVERLAY, name + ".AN"), "w", encoding="ascii") as f:
        # 官方格式:一行 = 一張圖 + 裁切框。這裡整張都要 → (0,0,w,h)。
        f.write("%s.png (0,0,%d,%d)" % (name, PLATE_W, PLATE_H))
    return png


def data_root():
    p = os.path.join(REPO, "data_root.txt")
    if os.path.isfile(p):
        with open(p, encoding="utf-8-sig") as f:
            v = f.read().strip()
            if v:
                return v
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="同步到 clean\\DATA(方便看效果;不是真相來源)")
    ap.add_argument("--preview", help="與官方 HOST 徽章併排放大輸出到這個 png")
    args = ap.parse_args()

    baked = {}
    for name, label in LABELS:
        img = bake(label)
        baked[name] = img
        print("烘好 %-8s %-8s → %s" % (name, label, write(name, img)))

    if args.apply:
        root = data_root()
        if not root:
            print("!! 找不到 data_root.txt，跳過 --apply")
        else:
            dst = os.path.join(root, OUT_REL)
            os.makedirs(dst, exist_ok=True)
            for name in baked:
                for ext in (".PNG", ".AN"):
                    shutil.copy2(os.path.join(OVERLAY, name + ext), os.path.join(dst, name + ext))
            # 訊息裡不放 emoji:Windows 的主控台是 cp950,印不出來會直接丟 UnicodeEncodeError,
            # 而那看起來像「腳本壞了」(其實檔案已經複製完了)。
            print("已同步到 %s(注意:build_clean_data.ps1 會蓋回,這裡只是為了看效果)" % dst)

    if args.preview:
        root = data_root()
        rows = []
        if root:
            host = os.path.join(root, OUT_REL, "B06.PNG")
            if os.path.isfile(host):
                rows.append(("官方 HOST", np.array(Image.open(host).convert("RGBA"))))
        for name in baked:
            rows.append((name, baked[name]))

        scale = 4
        width = max(r[1].shape[1] for r in rows) * scale
        canvas = Image.new("RGBA", (width, sum(r[1].shape[0] for r in rows) * scale), (90, 20, 80, 255))
        y = 0
        for name, img in rows:
            im = Image.fromarray(img, "RGBA").resize(
                (img.shape[1] * scale, img.shape[0] * scale), Image.NEAREST)
            canvas.alpha_composite(im, (0, y))
            y += img.shape[0] * scale
        canvas.convert("RGB").save(args.preview)
        print("預覽:", args.preview)


if __name__ == "__main__":
    main()
