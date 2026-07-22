# -*- coding: utf-8 -*-
"""
song_table.py — 「全部歌曲資料只有一份」的讀寫模組：StreamingAssets/song_table.csv。

取代掉原本四份互相對照的 JSON：
  gn_header_catalog.json  (每個 .gn 表頭：難度/音符數/長度/簡繁英歌名/曲師)
  gn_keytable.json        (每個 .gn 的解密資訊：enc/seed/innerOff/size)
  song_catalog.json       (遊戲歌單：由表頭衍生的難度/音符數)
  song_name_overrides.json + .csv (手改的歌名/BPM/offset)
四份都以 .gn 檔名為 key，內容大量重疊、又各有各的重建工具 → 很容易對不起來。
現在只有一張表，**一列 = 一個 .gn 檔**，遊戲跟工具都只讀這一份。

一首歌在原始資料裡有兩份譜（sdomNNNN**k**.gn 鍵盤 / **t**.gn 毯子），fileId 不同、難度不同，
所以它們是兩列；但「顯示用」的欄位（title/artist/bpm/offsetMs/src）兩列相同 ——
runtime 以 k 列為準（見 C# SongTable.Display），手改歌名改 k 列那一行就會生效。

欄位順序前四欄固定 fileId,title,artist,producer（人看的），其餘依「顯示 → 譜面 → 原文 → 解密」分組。
檔案是 UTF-8-BOM（Excel 繁中直接開不亂碼）、以 gn 升冪排序、LF 換行。
"""
from __future__ import annotations

import csv
import io
import os
from pathlib import Path
from typing import Dict, Iterable, List, Optional

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
SA = REPO / "65" / "My project" / "Assets" / "StreamingAssets"
DEFAULT_CSV = SA / "song_table.csv"

#: 欄位順序 = CSV 的表頭列。新增欄位請往後接（runtime 依名字取值，多/缺欄都不會壞）。
COLUMNS: List[str] = [
    # ── 顯示（k/t 兩列相同；這是唯一「人手改」的區塊）────────────────────────────
    "fileId",       # 歌曲編號：封面 NNNN.PNG / 試聽 exper/NNNN.ogg / 編舞 DANCE/NNNN.DPS 都用它
    "title",        # 顯示歌名（繁體）
    "artist",       # 顯示歌手
    "producer",     # 譜師（.gn 表頭的 producer 欄）
    "gn",           # ★ key：.gn 檔名（小寫，例 sdom0001k.gn）
    "mode",         # K = 鍵盤譜、T = 毯子譜
    "bpm",          # 顯示 BPM（選歌/房間標籤用；遊戲判定與流速一律讀譜面本身）
    "offsetMs",     # 這首的音訊校正：正 = 音樂晚進來、負 = 提早、0 = 不動（唯一真的進遊戲的手改欄）
    "src",          # 歌名來源標記（songname/official/kgn/manual），僅供辨識
    # ── 譜面數值（每個 .gn 各自不同；工具重掃會覆蓋，別手改）────────────────────
    "lvEasy", "lvNormal", "lvHard",
    "notesEasy", "notesNormal", "notesHard",
    "durEasy", "durNormal", "durHard",          # 秒
    "measEasy", "measNormal", "measHard",       # 小節數
    "chartBpm",     # .gn 表頭原始 BPM（bpm 欄是可手改的顯示值，這欄是資料原樣）
    # ── 原文歌名（.gn 表頭 GB2312 → UTF-8；title/artist 是它們的繁體/校正版）────
    "titleZhCn", "artistZhCn", "titleEn", "artistEn",
    "origName",     # 表頭裡的原始檔名（例 sdom0001K.gm）
    # ── 解密（GnChart 解 .gn 用；工具重掃會覆蓋，別手改）────────────────────────
    "enc",          # sdom / rewu / ddrm / plain / unknown
    "seed", "seed1", "seed2", "innerOff", "size",
]

#: 整數欄（空字串 = 沒有值）
INT_COLS = {
    "fileId", "lvEasy", "lvNormal", "lvHard",
    "notesEasy", "notesNormal", "notesHard",
    "durEasy", "durNormal", "durHard",
    "measEasy", "measNormal", "measHard",
    "seed", "seed1", "seed2", "innerOff", "size",
}
#: 浮點欄
FLOAT_COLS = {"bpm", "offsetMs", "chartBpm"}

#: 顯示區塊：同一首歌的 k/t 兩列必須一致（寫入時自動同步，見 sync_display）。
#  producer **不在**這裡：k 譜與 t 譜常常是不同人打的（sdom0002k=Tina / t=S.Q.H），那是每份譜自己的資料。
DISPLAY_COLS = ["title", "artist", "bpm", "offsetMs", "src"]


# ─────────────────────────────── key helpers ────────────────────────────────

def stem(gn: str) -> str:
    """.gn 檔名 → 歌曲詞幹（兩份譜共用）：'sdom0001k.gn' → 'sdom0001'。也是 .ogg 的檔名。"""
    n = (gn or "").strip().lower()
    if n.endswith(".gn"):
        n = n[:-3]
    if n and n[-1] in ("k", "t"):
        n = n[:-1]
    return n


def is_primary(gn: str) -> bool:
    """是不是鍵盤譜（k）—— 給人瀏覽的清單只該出現 k，否則每首歌會出現兩次。"""
    n = (gn or "").strip().lower()
    if n.endswith(".gn"):
        n = n[:-3]
    return bool(n) and n[-1] == "k"


# ────────────────────────────── value coercion ──────────────────────────────

def fmt(col: str, value) -> str:
    """欄位值 → CSV 字串。None/空 → ''；整數型浮點寫成 139 而不是 139.0（diff 好看）。"""
    if value is None or value == "":
        return ""
    if col in INT_COLS:
        try:
            return str(int(value))
        except (TypeError, ValueError):
            return ""
    if col in FLOAT_COLS:
        try:
            f = float(value)
        except (TypeError, ValueError):
            return ""
        if f != f:                       # NaN
            return ""
        return str(int(f)) if float(f).is_integer() else repr(round(f, 6))
    return str(value)


def _parse(col: str, raw: str):
    raw = (raw or "").strip()
    if raw == "":
        return None if (col in INT_COLS or col in FLOAT_COLS) else ""
    if col in INT_COLS:
        try:
            return int(float(raw))
        except ValueError:
            return None
    if col in FLOAT_COLS:
        try:
            return float(raw)
        except ValueError:
            return None
    return raw


def blank_row(gn: str) -> Dict:
    """一列空白資料（除了 gn）。給「新歌先佔位、之後各工具填欄」用。"""
    row = {c: (None if (c in INT_COLS or c in FLOAT_COLS) else "") for c in COLUMNS}
    row["gn"] = (gn or "").strip().lower()
    return row


# ──────────────────────────────── load / save ───────────────────────────────

def load(path: Path = DEFAULT_CSV) -> List[Dict]:
    """讀成 list of dict（依檔案順序）。檔案不存在 → 空 list。未知欄位一律保留（不會被吃掉）。"""
    p = Path(path)
    if not p.is_file():
        return []
    rows: List[Dict] = []
    with io.open(p, "r", encoding="utf-8-sig", newline="") as f:
        for raw in csv.DictReader(f):
            gn = (raw.get("gn") or "").strip().lower()
            if not gn:
                continue
            row = {c: _parse(c, raw.get(c, "")) for c in COLUMNS}
            for k, v in raw.items():                      # 保留表頭以外的欄位
                if k and k not in row:
                    row[k] = v
            row["gn"] = gn
            rows.append(row)
    return rows


def by_gn(rows: Iterable[Dict]) -> Dict[str, Dict]:
    """gn 檔名（小寫）→ 該列。"""
    return {(r.get("gn") or "").lower(): r for r in rows if r.get("gn")}


class TableLocked(OSError):
    """表被別的程式獨佔開著（Windows 上幾乎一定是 Excel）。"""


def check_writable(path: Path = DEFAULT_CSV) -> None:
    """確認表待會寫得動；寫不動就**在動任何東西之前**丟 TableLocked。

    為什麼要有這支：Excel 一開這個 .csv 就對它上獨佔鎖，Windows 會讓寫入直接 Errno 13。
    而「刪歌」是先刪檔案再寫表 —— 沒有這個前置檢查的話，表寫失敗時檔案已經刪光了，
    那首歌就變成「清單上還在、點下去沒譜沒音樂」的半殘狀態（實際發生過：sdom5002）。"""
    p = Path(path)
    if not p.is_file():
        return
    try:
        with io.open(p, "r+b"):
            pass
    except OSError as e:
        raise TableLocked(
            f"{p.name} 現在寫不進去（{e.strerror or e}）。\n"
            f"多半是你用 Excel 開著它 —— 關掉 Excel 再試一次。\n{p}") from e


def save(rows: Iterable[Dict], path: Path = DEFAULT_CSV) -> int:
    """寫回 CSV（gn 升冪排序、UTF-8-BOM、LF）。回傳列數。

    先寫同目錄的暫存檔再 os.replace 換過去（不是就地覆寫）—— 寫到一半斷電/被鎖，
    留下的是完整的舊表而不是一份被截斷的 4325 列歌單。"""
    rows = sync_display(list(rows))
    rows.sort(key=lambda r: (r.get("gn") or ""))
    cols = list(COLUMNS)
    for r in rows:                                        # 保留額外欄位（接在最後）
        for k in r:
            if k not in cols:
                cols.append(k)
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    check_writable(p)
    tmp = p.with_suffix(p.suffix + ".tmp")
    with io.open(tmp, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(cols)
        for r in rows:
            w.writerow([fmt(c, r.get(c)) for c in cols])
    try:
        os.replace(tmp, p)          # 同一顆磁碟上是原子操作
    except OSError:
        tmp.unlink(missing_ok=True)   # 換不過去(被鎖)就別留一個半調子的 .tmp 在那
        raise
    return len(rows)


def sync_display(rows: List[Dict]) -> List[Dict]:
    """把每首歌的顯示欄位（歌名/歌手/BPM/offset）同步到 k/t 兩列 —— **以 k 列為準**。

    人手改歌名時只會改到一列（通常是清單上看得到的 k），另一列若留舊值，
    用 t 譜查名字就會查到不一致的東西。寫檔前統一。"""
    primary: Dict[str, Dict] = {}
    for r in rows:
        gn = r.get("gn") or ""
        if is_primary(gn):
            primary[stem(gn)] = r
    for r in rows:
        src = primary.get(stem(r.get("gn") or ""))
        if src is None or src is r:
            continue
        for c in DISPLAY_COLS:
            r[c] = src.get(c)
    return rows
