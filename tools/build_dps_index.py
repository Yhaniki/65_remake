# -*- coding: utf-8 -*-
"""建立「生成舞蹈」用的索引表 DANCE/DPSINDEX.TXT。

外部歌（osu / StepMania）沒有官方 .dps，遊戲會現場生一支（Sdo.Osu.RandomDps）。生成需要三樣東西，
都只跟資料樹有關、不會隨玩家變動 —— 所以在這裡先掃好、烘成一個索引檔，遊戲開機不再掃目錄：

  P  動作池：MOTION/ 底下的通用舞步 wdanceNNNN.mot（排除 _CHANGJING 場景循環、EUR_ 等變體）。
  I  intro ：一支官方 DANCE/*.dps 的開場 —— 從第一個 row 一路收到「出現第四個不同的 mot」為止的所有 row。
             官方一個 row 是某支 clip 的一個「切片」(startFrame..endFrame)，開場常常是同一支 clip 連著切
             好幾段，所以連 frame range 一起烘；生成時逐 row 照抄，動作才會接得起來（只記名字會變成
             「整支重播」＝把整首歌吃掉又硬跳）。mot 檔不存在的 intro 直接丟掉（舞者會卡住）。
  F  幀數  ：每支被引用到的 .mot 有幾幀（= 檔尾 max_time float + 1），決定一個 row 能鋪幾拍。

用法：
    python tools/build_dps_index.py [--root assets/sdox_offline/Extracted] [--check]

--check 只印統計不寫檔。輸出檔會被 tools/package_build.ps1 隨 Extracted 一起複製進 DATA/。
"""
from __future__ import annotations

import argparse
import re
import statistics
import struct
import sys
from pathlib import Path

INDEX_REL = Path("DANCE") / "DPSINDEX.TXT"
VERSION = 2                    # V2 = intro 帶 frame range（V1 只有名字）
INTRO_MOTS = 3                 # 開場收到第 4 個不同的 mot 就停
INTRO_MAX_ROWS = 40            # 安全上限（A/B 交替的譜可以拖很長）
DPS_MAGIC = b"PAS00003"
ROW_STRIDES = (305, 317)       # 引擎 DpsLoader 認得的兩種 row 間距
POOL_RE = re.compile(r"^wdance\d+\.mot$", re.IGNORECASE)
MOT_RE = re.compile(rb"\.[mM][oO][tT]")
NAME_BYTES = set(b"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_")


def mot_frames(path: Path) -> int | None:
    """.mot 的幀數 = 檔尾 max_time float + 1（見 Assets/Scripts/Game/MotLoader.cs）。"""
    try:
        if path.stat().st_size < 20:
            return None
        with path.open("rb") as f:
            f.seek(-4, 2)
            (max_time,) = struct.unpack("<f", f.read(4))
    except Exception:
        return None
    if not (0.0 <= max_time <= 20000.0):
        return None
    return max(1, int(max_time) + 1)


def dps_rows(data: bytes, limit: int):
    """一支 .dps 的前 limit 個 row：(mot, startFrame, endFrame)。

    跟引擎一樣用 ".mot" 字串 + row 間距定位 row（row = preamble 12 + 名字 16 + mid），
    幀範圍在 mid 的 244 / 248。
    """
    rows = []
    prev = -1
    for m in MOT_RE.finditer(data):
        i = m.start()
        ns = i
        while ns > 0 and data[ns - 1] in NAME_BYTES:
            ns -= 1
        if ns == i:
            continue
        rs = ns - 12
        if rs < 12 or rs + 28 + 256 > len(data):
            continue
        if prev >= 0 and (rs - prev) not in ROW_STRIDES:
            continue                      # row 的 mid 區塊裡剛好有 ".mot" —— 不是 row 開頭
        prev = rs
        start, end = struct.unpack_from("<II", data, rs + 28 + 244)
        rows.append((data[ns:i + 4].decode("ascii", "ignore").lower(), start, end))
        if len(rows) >= limit:
            break
    return rows


def intro_of(path: Path, mots: dict[str, Path], frames: dict[str, int]):
    """一支官方 .dps 的開場：收 row 直到出現第 4 個不同的 mot。壞掉/收不滿三個 mot → None。"""
    try:
        data = path.read_bytes()
    except Exception:
        return None
    if not data.startswith(DPS_MAGIC):
        return None                       # 25 支 PAS00002 舊檔，引擎本來就不吃

    intro, seen = [], []
    for name, start, end in dps_rows(data, INTRO_MAX_ROWS + 1):
        if name not in seen:
            if len(seen) == INTRO_MOTS:
                break                     # 第 4 個不同的 mot → 開場結束
            seen.append(name)
        if name not in mots:
            return None                   # 這支 clip 沒隨資料附上（部分譜專屬的 w_*）→ 整組不能用
        n = frames.get(name)
        if end < start or (n is not None and end >= n):
            return None                   # 壞掉的幀範圍會讓舞者卡在最後一幀
        intro.append((name, start, end))
        if len(intro) >= INTRO_MAX_ROWS:
            break
    if len(seen) < INTRO_MOTS or not intro:
        return None
    return intro


def build(root: Path):
    mots: dict[str, Path] = {}
    for d in (root / "MOTION", root / "AUMOTION"):
        if not d.is_dir():
            continue
        for f in d.glob("*.[mM][oO][tT]"):
            mots.setdefault(f.name.lower(), f)     # MOTION 先，AUMOTION 只補沒有的

    pool = sorted(n for n in mots if POOL_RE.match(n))
    frames: dict[str, int] = {}
    for name in pool:
        n = mot_frames(mots[name])
        if n:
            frames[name] = n

    intros, seen_key = [], set()
    dropped = 0
    dance_dir = root / "DANCE"
    for f in sorted(dance_dir.glob("*.[dD][pP][sS]")) if dance_dir.is_dir() else []:
        # intro 的 mot 不限於 pool（開場常是 wrest / 譜專屬的 w_*）→ 需要時才補算幀數
        intro = intro_of(f, mots, frames)
        if intro is None:
            dropped += 1
            continue
        for name, _, _ in intro:
            if name not in frames:
                n = mot_frames(mots[name])
                if n:
                    frames[name] = n
        key = "|".join(f"{n}:{s}:{e}" for n, s, e in intro)
        if key in seen_key:
            continue
        seen_key.add(key)
        intros.append(intro)

    if intros:
        rows = [len(i) for i in intros]
        print(f"[dpsindex] intro rows: median {statistics.median(rows):.0f} max {max(rows)}")
    print(f"[dpsindex] motions {len(mots)} / pool {len(pool)} / intros {len(intros)} "
          f"(dropped {dropped}) / frames {len(frames)}")
    return pool, intros, frames


def render(pool, intros, frames) -> str:
    out = [
        "# SDO dance index - generated by tools/build_dps_index.py; do not hand-edit.",
        "# P=<motion>  I=<motion>:<startFrame>:<endFrame>|... (one official dance's opening rows, verbatim)",
        "# F=<motion> <frames>",
        f"V {VERSION}",
    ]
    out += [f"F {n} {frames[n]}" for n in sorted(frames)]
    out += [f"P {n}" for n in pool]
    out += ["I " + "|".join(f"{n}:{s}:{e}" for n, s, e in intro) for intro in intros]
    return "\n".join(out) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default="assets/sdox_offline/Extracted", help="資料樹根目錄（含 DANCE/ 與 MOTION/）")
    ap.add_argument("--check", action="store_true", help="只統計，不寫檔")
    args = ap.parse_args()

    root = Path(args.root)
    if not (root / "MOTION").is_dir():
        print(f"[dpsindex] 找不到 {root}/MOTION —— 用 --root 指到資料樹", file=sys.stderr)
        return 1

    pool, intros, frames = build(root)
    if not pool:
        print("[dpsindex] 動作池是空的（沒有 wdanceNNNN.mot）", file=sys.stderr)
        return 1
    if args.check:
        return 0

    out = root / INDEX_REL
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(render(pool, intros, frames), encoding="ascii")
    print(f"[dpsindex] wrote {out} ({out.stat().st_size / 1024:.0f} KB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
