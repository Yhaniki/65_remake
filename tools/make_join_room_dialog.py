#!/usr/bin/env python3
r"""把官方的「密碼輸入」對話框抹成空白版,給重製版的「輸入房號」用。

為什麼是這張:
    官方線上版有 UI/LOBBYDLG/PASSWORDDLG —— 進有密碼的房間時彈的那個框(標題「密码输入」+
    一列輸入欄 + 確認/取消)。重製版要的「輸入 5 位數房號」與它是同一種東西:一個輸入框加兩顆鈕。
    所以框體直接用官方的,不自己畫 —— 自己畫的框放在原版畫面上一眼就看得出來不是同一套。

為什麼只抹字、不烘新字:
    框上烘死的是簡體「密码输入」與欄位標籤「密　码」。重製版的做法(與 OPTIONDLG 那套一致)是
    **抹掉烘死的中文 + 執行期疊動態 TMP** —— 好處是字串能走本地化、也不必去猜官方用的是哪顆字型。
    抹字用 cv2.inpaint:標題列每一橫排的顏色橫向完全均勻(實測 std=0.00)、膠囊內是純色、
    鈕的底盤是垂直漸層,三種都是 inpaint 最擅長的情況。

輸出(art/generated/UI/LOBBYSEL/,與新按鈕同一棵樹 → 不用再改打包腳本):
    JOINDLG0        290x118  對話框框體(標題與欄位標籤都已抹空)
    JOINDLG1/2/3     54x24   鈕的底盤 normal/hover/pushed(字抹掉了)
    ⚠️ 官方的 确认(PASSWORD2/3/4) 與 取消(PASSWORD5/6/7) 抹掉字之後**逐位元組相同**,
       所以只出一組三態,兩顆鈕共用。這件事有 assert 守著:哪天不成立會直接爆,而不是靜默出錯。

放在 UI/LOBBYSEL 而不是 UI/LOBBYDLG 是刻意的:
    這個框是「選男女畫面(LOBBYSEL)按加入房間」時彈的,而 LobbySelArt 已經會解析那個資料夾 ——
    載入端零新程式、打包端零新步驟。官方的 LOBBYDLG 目前只有 KEYS 子樹會被打包進去。

執行期要用到的量測值(都寫進 JoinRoomModal.cs 的常數,這裡一併印出來對照):
    標題墨水      x=112..176 y=6..19      橘色 #A24D05..#EA8E0F(垂直漸層)+ 白色落影
    欄位標籤墨水  x=18..71   y=49..62     奶油色 #FFD117,深色描邊 #2E2428
    膠囊(標籤底) x=14..79   y=47..65     內部純色 #EEB5CD
    粉紅輸入區    x=82..277  y=47..65     #EAA2C0
    鈕上的字      x=12..40   y=5..15      normal #182445 / hover·pushed #5FB002(pushed 再往下 1px)

依賴: pip install opencv-python pillow numpy

用法:
    python tools/make_join_room_dialog.py
    python tools/make_join_room_dialog.py --apply             # 順便同步到 clean\DATA
    python tools/make_join_room_dialog.py --preview out.png   # 官方 vs 抹空 併排放大
"""
import argparse
import os
import shutil
import subprocess
import sys

import numpy as np
from PIL import Image

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_REL = os.path.join("UI", "LOBBYSEL")
OVERLAY = os.path.join(REPO, "art", "generated", OUT_REL)
SRC_REL = os.path.join("UI", "LOBBYDLG")

PANEL_L, PANEL_R = "LOBBYDLG_PASSWORD0", "LOBBYDLG_PASSWORD1"   # 256x118 + 34x118 = 290x118
OK_STATES = ["LOBBYDLG_PASSWORD2", "LOBBYDLG_PASSWORD3", "LOBBYDLG_PASSWORD4"]      # 确认
CANCEL_STATES = ["LOBBYDLG_PASSWORD5", "LOBBYDLG_PASSWORD6", "LOBBYDLG_PASSWORD7"]  # 取消

# 抹字範圍(x0,y0,x1,y1,1x 像素,含)。比量到的墨水框各留 2~4 格餘裕蓋掉 AA,
# 但不能大到碰上結構物:標題不能碰左右的圓角、膠囊不能碰白色外框、鈕不能碰膠囊兩端的圓角。
TITLE_BOX = (106, 4, 182, 22)        # 墨水 x=112..176 y=6..19
PILL_BOX = (17, 48, 77, 64)          # 膠囊內部(白框在 x=14..15 / 78..79,不動)
PILL_FILL = (0xEE, 0xB5, 0xCD)
BTN_BOX = (11, 3, 41, 20)            # 墨水 x=12..40 y=5..16(pushed 態往下 1px)


def data_root():
    p = os.path.join(REPO, "data_root.txt")
    if not os.path.isfile(p):
        return None
    root = open(p, encoding="utf-8-sig").read().strip()
    return root if root and os.path.isdir(root) else None


def resolve_src_dir():
    """找官方 LOBBYDLG 原圖。worktree 裡沒有 assets/(那棵樹只在主 worktree),所以要往上找。"""
    cands = [os.path.join(REPO, "assets", "sdox_offline", "Extracted", SRC_REL)]
    try:
        common = subprocess.check_output(["git", "rev-parse", "--git-common-dir"],
                                         cwd=REPO, text=True).strip()
        main_wt = os.path.dirname(os.path.abspath(os.path.join(REPO, common)))
        cands.append(os.path.join(main_wt, "assets", "sdox_offline", "Extracted", SRC_REL))
    except Exception:
        pass
    # clean\DATA 只留 LOBBYDLG/KEYS,所以這裡不把它當來源 —— 找不到就是主 worktree 的 assets 沒掛上。
    for c in cands:
        if os.path.isfile(os.path.join(c, PANEL_L + ".PNG")):
            return c
    sys.exit("找不到官方 LOBBYDLG 原圖,找過:\n  " + "\n  ".join(cands))


def load(src, name):
    return np.array(Image.open(os.path.join(src, name + ".PNG")).convert("RGBA"))


def fill_rows(rgba, box, ref_cols):
    """把 box 內每一橫排,換成該排在 ref_cols 那幾欄的顏色(逐排各自算)。

    🔴 為什麼不用 cv2.inpaint:字是一塊接近實心的區域,Telea 只能從洞的邊界往裡猜 ——
    結果是一團霧(第一版就是這樣抹爛的)。但這三塊底的結構我們已經量過了:
    **每一橫排橫向完全均勻**(標題列實測 std=0.00,鈕的膠囊中段也是)。
    既然同一排的真值就在旁邊那幾欄,直接抄過來就是精確解,不必猜。
    """
    x0, y0, x1, y1 = box
    out = rgba.copy()
    for y in range(y0, y1 + 1):
        ref = np.median(rgba[y, ref_cols, :].astype(int), axis=0).astype(rgba.dtype)
        out[y, x0:x1 + 1, :] = ref
    return out


def blank_panel(src):
    """組出 290x118 的框,再把標題與欄位標籤的字抹掉。"""
    a, b = load(src, PANEL_L), load(src, PANEL_R)
    panel = np.concatenate([a, b], axis=1).astype(np.uint8)
    # 標題列:整排橫向均勻 → 拿同排框內(x=50..70,標題字左邊)的顏色填掉標題。
    panel = fill_rows(panel, TITLE_BOX, list(range(50, 71)))
    # 欄位標籤:膠囊內部是純色 #EEB5CD;白色外框在 box 之外,不會被動到。
    x0, y0, x1, y1 = PILL_BOX
    panel[y0:y1 + 1, x0:x1 + 1, :3] = PILL_FILL
    panel[y0:y1 + 1, x0:x1 + 1, 3] = 255
    return panel


def blank_button(src, name):
    """把鈕上的字抹掉,只留膠囊底盤(垂直漸層,中段橫向均勻)。"""
    b = load(src, name).astype(np.uint8)
    # 參考欄取字的左右兩側(x=9..10 / 43..44):再往外就碰到膠囊的圓角,顏色就不代表這一排了。
    return fill_rows(b, BTN_BOX, [9, 10, 43, 44])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="順便同步到 clean\\DATA")
    ap.add_argument("--preview", metavar="PNG", help="官方 vs 抹空 的放大對照圖")
    args = ap.parse_args()

    src = resolve_src_dir()
    print("[dlg] 原圖來源:", src)
    os.makedirs(OVERLAY, exist_ok=True)

    panel = blank_panel(src)
    plates = [blank_button(src, n) for n in OK_STATES]

    # 三態的真相(實測):hover 只換字的顏色、底盤與 normal 完全相同;
    # pushed 是**整顆往下位移 1px**(不是重畫的深色底盤)。這兩條 assert 把這個發現釘住 ——
    # 哪天來源拿錯或官方檔換了,這裡會直接爆而不是靜默出一組怪圖。
    d_hover = int(np.abs(plates[1].astype(int) - plates[0].astype(int)).max())
    assert d_hover <= 8, "hover 的底盤應該與 normal 相同(只換字色),卻差了 %d" % d_hover
    # 位移那條用 99 百分位而不是最大值:抹字時「兩邊同一格都有字」的少數像素是 inpaint 補的,
    # 那幾格本來就不會逐位元組相同(實測最大差 19)。要看的是「整體有沒有對上」。
    shifted = np.roll(plates[0], 1, axis=0)[2:-2]
    # 門檻放寬到 32 是刻意的:這條要抓的是「來源拿錯」(那種會差好幾百),不是逐位元組證明。
    d_push = float(np.percentile(np.abs(plates[2][2:-2].astype(int) - shifted.astype(int)), 99))
    assert d_push <= 32, "pushed 的底盤應該是 normal 往下位移 1px,99%% 的像素卻差了 %.1f" % d_push
    print("[dlg] 三態關係核對:hover 底盤 diff=%d(應為 0)、pushed 位移 1px 後 p99=%.1f"
          % (d_hover, d_push))

    out = [("JoinDlg0", panel)] + [("JoinDlg%d" % (i + 1), im) for i, im in enumerate(plates)]
    for name, im in out:
        Image.fromarray(im).save(os.path.join(OVERLAY, name.upper() + ".PNG"))
        with open(os.path.join(OVERLAY, name.upper() + ".AN"), "w", encoding="ascii") as f:
            f.write(name + ".png")
        print("[dlg] %s %dx%d" % (name.upper(), im.shape[1], im.shape[0]))

    if args.apply:
        root = data_root()
        if not root:
            sys.exit("--apply 需要 data_root.txt")
        dst = os.path.join(root, OUT_REL)
        for name, _ in out:
            for ext in (".PNG", ".AN"):
                shutil.copy2(os.path.join(OVERLAY, name.upper() + ext),
                             os.path.join(dst, name.upper() + ext))
        print("[dlg] 已同步到", dst)

    if args.preview:
        a, b = load(src, PANEL_L), load(src, PANEL_R)
        before = np.concatenate([a, b], axis=1)
        rows = [[before, load(src, OK_STATES[0]), load(src, CANCEL_STATES[1])],
                [panel, plates[0], plates[1]]]
        S = 3
        w = sum(im.shape[1] for im in rows[0]) * S + 40
        h = max(im.shape[0] for im in rows[0]) * S * 2 + 30
        sheet = Image.new("RGBA", (w, h), (32, 32, 40, 255))
        for r, row in enumerate(rows):
            x = 10
            for im in row:
                big = Image.fromarray(im).resize((im.shape[1] * S, im.shape[0] * S), Image.NEAREST)
                sheet.alpha_composite(big, (x, 10 + r * (rows[0][0].shape[0] * S + 10)))
                x += im.shape[1] * S + 10
        sheet.convert("RGB").save(args.preview)
        print("[dlg] 對照圖(上=官方 下=抹空):", args.preview)


if __name__ == "__main__":
    main()
