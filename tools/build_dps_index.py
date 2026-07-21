# -*- coding: utf-8 -*-
"""建立「生成舞蹈」用的索引表 DANCE/DPSINDEX.TXT。

外部歌（osu / StepMania）沒有官方 .dps，遊戲會現場生一支（Sdo.Osu.RandomDps）。生成需要的東西
都只跟資料樹有關、不會隨玩家變動 —— 所以在這裡先掃好、烘成一個索引檔，遊戲開機不再掃目錄：

  P  動作池：MOTION/ 底下的通用舞步 wdanceNNNN.mot（排除 _CHANGJING 場景循環、EUR_ 等變體）。沒有
             群組可用時（舊索引）才會用到，是最後的保險絲。
  I  開場  ：一支官方 DANCE/*.dps 的第一組 —— 從第一個 row 一路收到「出現第四個不同的 mot」為止的所有 row。
  G  群組  ：同一支 .dps 第一組以後的每一組（一樣是三支不同 mot 為一組）。生成時開頭挑一個 I、
             後面每次隨機挑一個 G，逐 row 照抄。
             官方一個 row 是某支 clip 的一個「切片」(startFrame..endFrame)，一組裡常常是同一支 clip 連著切
             好幾段，所以連 frame range 一起烘；生成時逐 row 照抄，動作才會接得起來（只記名字會變成
             「整支重播」＝把整首歌吃掉又硬跳）。clip 沒附上、或幀範圍超出 clip 長度的組直接丟掉（舞者會卡住）。
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
VERSION = 3                    # V3 = 開場之後的每一組也烘（G）；V2 = 只有開場且帶 frame range；V1 只有名字
GROUP_MOTS = 3                 # 一組 = 三支不同的 mot，收到第 4 個就換下一組
GROUP_MAX_ROWS = 40            # 一組的 row 上限（A/B 交替的譜可以拖很長）
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


def dps_rows(data: bytes, limit: int | None = None):
    """一支 .dps 的 row（limit=前幾個，None=整支）：(mot, startFrame, endFrame)。

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
        if limit is not None and len(rows) >= limit:
            break
    return rows


def groups_of(path: Path, frames_of):
    """一支官方 .dps 從頭切到尾的每一組（一組 = 三支不同的 mot）：[(序號, [(mot, start, end), ...]), ...]。

    序號 0 = 開場（會烘成 I），之後的是 G。壞掉的組（clip 沒附上／幀範圍超出 clip／row 太多）只丟那一組，
    不影響同一支檔的其他組；檔尾湊不滿三支不同 mot 的殘組也丟掉。回傳 (組, 丟掉幾組)。
    """
    try:
        data = path.read_bytes()
    except Exception:
        return [], 0
    if not data.startswith(DPS_MAGIC):
        return [], 0                      # 25 支 PAS00002 舊檔，引擎本來就不吃

    out, dropped, ordinal = [], 0, 0
    cur, seen, ok = [], [], True

    def close():
        nonlocal cur, seen, ok, dropped, ordinal
        if len(seen) == GROUP_MOTS:       # 湊滿三支才算一組（檔尾的殘組不要）
            if ok and cur:
                out.append((ordinal, cur))
            else:
                dropped += 1
            ordinal += 1
        cur, seen, ok = [], [], True

    for name, start, end in dps_rows(data):
        if name not in seen:
            if len(seen) == GROUP_MOTS:
                close()                   # 第 4 個不同的 mot → 這組收尾，它是下一組的第一支
            seen.append(name)
        if not ok:
            continue                      # 這組已經判定不能用，只要繼續數 mot 找組界
        n = frames_of(name)
        if n is None or end < start or end >= n or len(cur) >= GROUP_MAX_ROWS:
            ok = False                    # clip 沒附上（譜專屬的 w_*）／壞掉的幀範圍會讓舞者卡在最後一幀
            continue
        cur.append((name, start, end))
    close()
    return out, dropped


def build(root: Path):
    mots: dict[str, Path] = {}
    for d in (root / "MOTION", root / "AUMOTION"):
        if not d.is_dir():
            continue
        for f in d.glob("*.[mM][oO][tT]"):
            mots.setdefault(f.name.lower(), f)     # MOTION 先，AUMOTION 只補沒有的

    # 組裡的 mot 不限於 pool（常是 wrest / 譜專屬的 w_*）→ 用到才算幀數，算過就記著
    cache: dict[str, int | None] = {}

    def frames_of(name: str):
        if name not in cache:
            p = mots.get(name)
            cache[name] = mot_frames(p) if p is not None else None
        return cache[name]

    pool = [n for n in sorted(mots) if POOL_RE.match(n) and frames_of(n)]

    intros, groups, seen_key = [], [], set()
    dropped = 0
    dance_dir = root / "DANCE"
    for f in sorted(dance_dir.glob("*.[dD][pP][sS]")) if dance_dir.is_dir() else []:
        found, drop = groups_of(f, frames_of)
        dropped += drop
        for ordinal, g in found:
            key = "|".join(f"{n}:{s}:{e}" for n, s, e in g)
            if key in seen_key:
                continue                  # 同一段編舞在別支歌又出現一次 → 只留一份
            seen_key.add(key)
            (intros if ordinal == 0 else groups).append(g)

    # F 只寫真的會被讀到的：pool + 被烘出去的組引用到的 clip
    frames = {n: cache[n] for n in pool}
    for g in intros + groups:
        for name, _, _ in g:
            frames[name] = cache[name]

    for label, gs in (("intro", intros), ("group", groups)):
        if gs:
            rows = [len(g) for g in gs]
            print(f"[dpsindex] {label} rows: median {statistics.median(rows):.0f} max {max(rows)}")
    print(f"[dpsindex] motions {len(mots)} / pool {len(pool)} / intros {len(intros)} / groups {len(groups)} "
          f"(dropped {dropped}) / frames {len(frames)}")
    return pool, intros, groups, frames


def render(pool, intros, groups, frames) -> str:
    out = [
        "# SDO dance index - generated by tools/build_dps_index.py; do not hand-edit.",
        "# P=<motion>",
        "# I=<motion>:<startFrame>:<endFrame>|... (one official dance's OPENING rows, verbatim)",
        "# G=<motion>:<startFrame>:<endFrame>|... (one later 3-motion group of an official dance, verbatim)",
        "# F=<motion> <frames>",
        f"V {VERSION}",
    ]
    out += [f"F {n} {frames[n]}" for n in sorted(frames)]
    out += [f"P {n}" for n in pool]
    out += ["I " + "|".join(f"{n}:{s}:{e}" for n, s, e in intro) for intro in intros]
    out += ["G " + "|".join(f"{n}:{s}:{e}" for n, s, e in g) for g in groups]
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

    pool, intros, groups, frames = build(root)
    if not pool:
        print("[dpsindex] 動作池是空的（沒有 wdanceNNNN.mot）", file=sys.stderr)
        return 1
    if args.check:
        return 0

    out = root / INDEX_REL
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(render(pool, intros, groups, frames), encoding="ascii")
    print(f"[dpsindex] wrote {out} ({out.stat().st_size / 1024:.0f} KB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
