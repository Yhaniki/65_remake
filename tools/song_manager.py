# -*- coding: utf-8 -*-
"""
song_manager.py — 歌曲管理員(Tkinter GUI):看歌單、改歌名/BPM/音訊 offset、刪歌、插入新歌。

啟動:
    python tools/song_manager.py
(只用 Python 內建 tkinter，不必裝任何套件。)

它是既有 CLI 工具的殼,不是第二套邏輯:
    加歌 = add_songs_incremental.add_gn_files()
    刪歌 = remove_songs.remove_stems()
    改名 = 寫 song_table.csv 的 title/artist/bpm/offsetMs 欄
所以 GUI 做的每件事,用命令列也做得到、結果一模一樣。

三個概念先講清楚(這是所有坑的來源):
  1. 全部歌曲資料只有一份:song_table.csv,一列 = 一個 .gn 檔(同一首歌的 K/T 兩譜是兩列,
     顯示欄位同步)。「歌單有哪些歌」就是這張表有哪些列 —— 從表裡刪一列 = 那份譜真的不見了
     (不像以前刪 overrides 只是歌名變回 .gn 內嵌名)。title/artist/bpm 只影響顯示;
     offsetMs 是唯一真的會進遊戲的欄(音訊校正:正 = 音樂晚一點進來、負 = 提早,
     只挪音樂＋舞蹈,音符/判定仍讀譜面)。
  2. 兩個 key,各管一半:
       gn 檔名(詞幹) → 譜面、主音樂 sdomNNNN.ogg、收藏、歌名覆蓋
       fileId(整數)  → 試聽 exper/<id>.ogg、舞蹈 DANCE/<id>.DPS、封面 ICONS/<id>.PNG
     所以插隊加歌時,兩個都得挑沒人用的。
  3. 歌單顯示順序 = gn 檔名「字串」倒序,不是數字。要把新歌插在 sdom1150 旁邊(1110-1200
     全被占滿也沒關係),就取名 sdom1150_1 —— 字串序上 sdom1150_1k.gn < sdom1150k.gn,
     它會落在 1150 隔壁,而且一個既有檔案都不用改名。見「插在…之後」按鈕。
  4. 檔案寫到哪:song_table.csv(歌單/難度/歌名)永遠進 StreamingAssets;譜面/音樂/試聽/舞蹈/封面這些
     「檔案」則寫進所有 data 樹 —— repo 的 sdox_offline(來源),外加 data_root.txt / SDO_DATA_ROOT
     指到的乾淨 DATA(遊戲執行期實際讀的那棵)。沒設 data_root.txt 時就只有前者。解析規則跟遊戲
     runtime 完全一致(見 remove_songs.data_trees / SdoDataRoot.cs),不然加的歌遊戲會看不到檔案。
"""
from __future__ import annotations

import re
import shutil
import sys
import traceback
from pathlib import Path
from typing import Dict, List, Optional

import tkinter as tk
from tkinter import filedialog, messagebox, ttk

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from add_songs_incremental import add_gn_files                                   # noqa: E402
from gn_keytable import process_file                                             # noqa: E402
from remove_songs import (TABLE, data_trees, runtime_tree,                       # noqa: E402
                          load_catalog, remove_stems, stem)
import song_table as stbl                                                        # noqa: E402

MAX_OFFSET_MS = 5000.0                                                    # 同 runtime SongCatalog.MaxOffsetMs


# ---------------------------------------------------------------- 資料層

class Song:
    """一首歌(K/T 兩譜合成一列給人看),欄位取自該首的 K 譜那一列。"""

    def __init__(self, st: str, row: Dict, gns: List[str]):
        self.stem = st
        self.file_id = int(row.get("fileId") or 0)
        self.gns: List[str] = list(gns)
        self.title = row.get("title") or ""
        self.artist = row.get("artist") or ""
        self.bpm = float(row.get("bpm") or 0)
        self.src = row.get("src") or ""
        # 音訊校正(毫秒):正 = 音樂晚一點進來、負 = 提早、0 = 不動。只有這欄會真的進遊戲。
        self.offset_ms = float(row.get("offsetMs") or 0)

    def assets(self) -> str:
        """哪些資源檔在位(看遊戲實際讀的那棵樹 —— 有 data_root.txt 就是乾淨 DATA,否則 dev)。
        缺主音樂 = 開局會沒聲音,其餘缺了都有 fallback。"""
        f = self.file_id
        t = runtime_tree()
        marks = [
            ("♪", (t["music"] / f"{self.stem}.ogg").is_file()),              # 主音樂(缺 = 沒聲音)
            ("▶", (t["exper"] / f"{f}.ogg").is_file()),                      # 試聽(缺 = 退主音樂中段)
            ("◆", (t["dance"] / f"{f}.DPS").is_file()),                      # 舞蹈(缺 = 通用舞)
            ("★", (t["icons"] / f"{f}.PNG").is_file()
                  or (t["icons"] / f"{f}.png").is_file()),                   # 封面(缺 = 佔位圖)
        ]
        return "".join(m if ok else "·" for m, ok in marks)


def rows_by_stem() -> Dict[str, List[Dict]]:
    """{詞幹: [該首的每一列]}，K 譜排在最前面(顯示與資源都以 K 為準)。"""
    out: Dict[str, List[Dict]] = {}
    for r in stbl.load(TABLE):
        out.setdefault(stem(r["gn"]), []).append(r)
    for v in out.values():
        v.sort(key=lambda r: (not stbl.is_primary(r["gn"]), r["gn"]))
    return out


def load_songs() -> List[Song]:
    songs = [Song(s, rows[0], [r["gn"] for r in rows]) for s, rows in rows_by_stem().items()]
    songs.sort(key=lambda s: s.stem, reverse=True)   # 同遊戲:檔名字串倒序(新號在上)
    return songs


# ---------------------------------------------------------------- 空號配置

def used_slots() -> tuple[set[str], set[int]]:
    """已用的詞幹 + fileId。同時看歌曲表「和」music 目錄實體檔 —— 表裡沒收進去的殘骸
    也算占用,否則新歌會覆蓋掉它。"""
    stems, fids = set(), set()
    for r in stbl.load(TABLE):
        stems.add(stem(r["gn"]))
        fids.add(int(r.get("fileId") or 0))
    for tree in data_trees():                          # dev 樹 + 乾淨 DATA 的殘骸都算占用
        if tree["music"].is_dir():
            for p in tree["music"].glob("*.gn"):
                stems.add(stem(p.name))
    return stems, fids


def suggest_stem(base: str, stems: set[str]) -> str:
    """base 沒被占就用 base;被占了就 base_1 / base_2 …(字串序上剛好排在 base 隔壁)。"""
    if base not in stems:
        return base
    for i in range(1, 100):
        cand = f"{base}_{i}"
        if cand not in stems:
            return cand
    raise RuntimeError(f"{base} 的插隊號用完了")


def suggest_file_id(fids: set[int]) -> int:
    """新 fileId = 目前最大 + 1。刻意不撿中間的空號 —— 那些號碼可能還躺著上一首被刪歌
    留下的孤兒 DPS/封面,撿了會被那些舊資源黏上。"""
    return (max(fids) if fids else 10000) + 1


def add_song(*, k: Path, stem_: str, file_id: int, ogg: Path, t: Optional[Path] = None,
             exper: Optional[Path] = None, dps: Optional[Path] = None, icon: Optional[Path] = None,
             title: str = "", artist: str = "", bpm: str = "") -> List[str]:
    """加一首歌:驗號 → 驗譜面解得開 → 複製改名 → 更新 song_table.csv → (可選)寫歌名/BPM。

    GUI 的「加入」鈕就是呼叫這支;失敗會把已複製的檔案收回來,不留半套。
    檔名規則(兩個 key,各管一半):
        譜面 {stem}K.gn / {stem}T.gn、主音樂 {stem}.ogg      ← gn 詞幹
        試聽 exper/{fileId}.ogg、舞蹈 DANCE/{fileId}.DPS、
        封面 ICONS/{fileId}.PNG                              ← fileId
    """
    stems, fids = used_slots()
    st = stem_.strip().lower()
    if not re.fullmatch(r"sdom[\d_]+", st):
        raise ValueError("詞幹要長成 sdomNNNN(插隊可加 _N，例 sdom1150_1)")
    if st in stems:
        raise ValueError(f"{st} 已經有人用了,換一個")
    if file_id in fids:
        raise ValueError(f"fileId {file_id} 已經有人用了(它是試聽/舞蹈/封面的 key),換一個")
    if not k.is_file():
        raise ValueError("K 譜找不到")
    if t and not t.is_file():
        raise ValueError("T 譜找不到")
    if not ogg or not ogg.is_file():
        raise ValueError("主音樂 .ogg 找不到(沒有它開局會沒聲音)")

    # 先驗譜面解得開,再動手複製 —— 免得解不開卻已經在 music/ 留下半套檔案。
    for p in [x for x in (k, t) if x]:
        e = process_file(p.read_bytes(), p.name, {})
        if e.get("enc") in (None, "unknown", "sdom_failed", "error"):
            raise ValueError(f"{p.name} 解不開(enc={e.get('enc')}) — 不是有效的 .gn?")

    trees = data_trees()                       # dev 原始樹 +(若設了 data_root.txt)遊戲實際讀的乾淨 DATA
    copied: List[Path] = []
    try:
        def cp(src: Path, dst: Path):
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(src, dst)
            copied.append(dst)

        k_dst = t_dst = None                    # 每棵樹都複製一份;交給 add_gn_files 讀的取 dev 樹那份即可
        for tree in trees:
            kk = tree["music"] / f"{st}K.gn"; cp(k, kk); k_dst = k_dst or kk
            if t:
                tt = tree["music"] / f"{st}T.gn"; cp(t, tt); t_dst = t_dst or tt
            cp(ogg, tree["music"] / f"{st}.ogg")
            for src, dst in ((exper, tree["exper"] / f"{file_id}.ogg"),
                             (dps, tree["dance"] / f"{file_id}.DPS"),
                             (icon, tree["icons"] / f"{file_id}.PNG")):
                if src:
                    cp(src, dst)

        log = add_gn_files([k_dst] + ([t_dst] if t_dst else []), force=False, dry_run=False, file_id=file_id)
    except Exception:
        for p in copied:                       # 失敗就把複製過去的檔收回來,不留半套
            try:
                p.unlink()
            except OSError:
                pass
        raise
    if len(trees) > 1:                          # 有 data_root.txt 覆寫 → 明講檔案也寫進了那棵樹
        log.append(f"  檔案已同步 {len(trees)} 棵資料樹(含 data_root.txt → {trees[-1]['dance'].parent})")

    if title or artist or bpm.strip():
        # add_gn_files 剛剛才寫過表(歌名是 .gn 表頭轉繁的打底值),所以要重讀再蓋上你填的。
        # 一首歌的 K/T 兩列共用顯示值 → 兩列都寫(stbl.save 也會再同步一次)。
        table = stbl.load(TABLE)
        hit = [r for r in table if stem(r["gn"]) == st]
        for row in hit:
            if title:
                row["title"] = title
            if artist:
                row["artist"] = artist
            if bpm.strip():
                row["bpm"] = round(float(bpm), 3)
            row["src"] = "manual"          # 標記手改 → build_song_name_overrides 重跑時會保留
        if hit:
            stbl.save(table, TABLE)
            log.append(f"  歌名/BPM 手改 {st}({len(hit)} 列)")
        else:
            log.append(f"  [warn] 表裡找不到 {st},歌名沒寫進去")
    return log


# ---------------------------------------------------------------- GUI

class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("SDO 歌曲管理員")
        self.geometry("1180x680")
        self.songs: List[Song] = []
        self.shown: List[Song] = []
        self._build()
        self.reload()

    # ---- 版面
    def _build(self):
        top = ttk.Frame(self, padding=(8, 8, 8, 4))
        top.pack(fill="x")
        ttk.Label(top, text="搜尋").pack(side="left")
        self.q = tk.StringVar()
        self.q.trace_add("write", lambda *_: self.refresh())
        e = ttk.Entry(top, textvariable=self.q, width=32)
        e.pack(side="left", padx=(4, 12))
        e.focus()
        ttk.Button(top, text="新增歌曲…", command=self.on_add).pack(side="left")
        ttk.Button(top, text="插在選取那首之後…", command=lambda: self.on_add(insert_after=True)).pack(side="left", padx=4)
        ttk.Button(top, text="刪除選取", command=self.on_delete).pack(side="left")
        ttk.Button(top, text="重新載入", command=self.reload).pack(side="left", padx=4)
        self.count = ttk.Label(top, text="")
        self.count.pack(side="right")

        cols = ("stem", "fid", "title", "artist", "bpm", "offset", "diff", "assets")
        self.tree = ttk.Treeview(self, columns=cols, show="headings", selectmode="extended")
        for c, txt, w in (("stem", "詞幹", 110), ("fid", "fileId", 70), ("title", "歌名", 300),
                          ("artist", "歌手", 170), ("bpm", "BPM", 60), ("offset", "offset(ms)", 80),
                          ("diff", "難度 E/N/H", 90),
                          ("assets", "音樂/試聽/舞蹈/封面", 150)):
            self.tree.heading(c, text=txt)
            self.tree.column(c, width=w, anchor="w" if c in ("title", "artist") else "center")
        vs = ttk.Scrollbar(self, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vs.set)
        self.tree.pack(side="top", fill="both", expand=True, padx=8)
        vs.place(in_=self.tree, relx=1.0, rely=0, relheight=1.0, anchor="ne")
        self.tree.bind("<<TreeviewSelect>>", self.on_select)

        ed = ttk.LabelFrame(self, text="編輯(寫進 song_table.csv;歌名/歌手/BPM 只影響顯示，offset 會真的挪音樂)",
                            padding=8)
        ed.pack(fill="x", padx=8, pady=8)
        self.f_title, self.f_artist = tk.StringVar(), tk.StringVar()
        self.f_bpm, self.f_offset = tk.StringVar(), tk.StringVar()
        ttk.Label(ed, text="歌名").grid(row=0, column=0, sticky="e")
        ttk.Entry(ed, textvariable=self.f_title, width=36).grid(row=0, column=1, padx=(4, 12))
        ttk.Label(ed, text="歌手").grid(row=0, column=2, sticky="e")
        ttk.Entry(ed, textvariable=self.f_artist, width=22).grid(row=0, column=3, padx=(4, 12))
        ttk.Label(ed, text="BPM").grid(row=0, column=4, sticky="e")
        ttk.Entry(ed, textvariable=self.f_bpm, width=8).grid(row=0, column=5, padx=(4, 12))
        ttk.Label(ed, text="offset(ms)").grid(row=0, column=6, sticky="e")
        ttk.Entry(ed, textvariable=self.f_offset, width=8).grid(row=0, column=7, padx=(4, 12))
        ttk.Button(ed, text="套用修改", command=self.on_apply_edit).grid(row=0, column=8)
        ttk.Label(ed, foreground="#666",
                  text="offset = 這首歌的音訊校正：正值 = 音樂晚一點進來(音樂跑太前面、音符老是慢半拍時用)，"
                       "負值 = 音樂提早，留空/0 = 不動。只挪音樂＋舞蹈，音符與判定不受影響。").grid(
            row=1, column=0, columnspan=9, sticky="w", pady=(6, 0))
        self.status = ttk.Label(ed, text="", foreground="#0a7")
        self.status.grid(row=2, column=0, columnspan=9, sticky="w", pady=(4, 0))

    # ---- 資料
    def reload(self):
        try:
            self.songs = load_songs()
        except Exception as ex:
            messagebox.showerror("讀取失敗", f"{ex}\n\n{traceback.format_exc()}")
            self.songs = []
        self.refresh()

    def refresh(self):
        q = self.q.get().strip().lower()
        self.shown = [s for s in self.songs
                      if not q or q in s.title.lower() or q in s.artist.lower()
                      or q in s.stem or q in str(s.file_id)]
        self.tree.delete(*self.tree.get_children())
        table = stbl.by_gn(stbl.load(TABLE))
        for i, s in enumerate(self.shown):
            row = next((table[g] for g in s.gns if g in table and stbl.is_primary(g)), {})
            diff = "/".join(str(row.get(k) or 0) for k in ("lvEasy", "lvNormal", "lvHard"))
            self.tree.insert("", "end", iid=str(i), values=(
                s.stem, s.file_id, s.title, s.artist,
                (f"{s.bpm:g}" if s.bpm > 0 else ""),
                (f"{s.offset_ms:+g}" if s.offset_ms else ""), diff, s.assets()))
        self.count.config(text=f"{len(self.shown)} / {len(self.songs)} 首")

    def selected(self) -> List[Song]:
        return [self.shown[int(i)] for i in self.tree.selection()]

    def on_select(self, _evt=None):
        sel = self.selected()
        if len(sel) != 1:
            return
        s = sel[0]
        self.f_title.set(s.title)
        self.f_artist.set(s.artist)
        self.f_bpm.set(f"{s.bpm:g}" if s.bpm > 0 else "")
        self.f_offset.set(f"{s.offset_ms:g}" if s.offset_ms else "")

    # ---- 改名 / 改 BPM / 改 offset
    def on_apply_edit(self):
        sel = self.selected()
        if len(sel) != 1:
            messagebox.showinfo("改哪首?", "請先在清單選一首(單選)。")
            return
        s = sel[0]
        bpm_txt = self.f_bpm.get().strip()
        try:
            bpm = round(float(bpm_txt), 3) if bpm_txt else 0.0
        except ValueError:
            messagebox.showerror("BPM 不是數字", f"「{bpm_txt}」不是數字。留空 = 沿用譜面的 BPM。")
            return
        off_txt = self.f_offset.get().strip()
        try:
            off_ms = round(float(off_txt), 1) if off_txt else 0.0
        except ValueError:
            messagebox.showerror("offset 不是數字", f"「{off_txt}」不是數字。留空/0 = 不位移音樂。")
            return
        if abs(off_ms) > MAX_OFFSET_MS:        # 同 runtime 的夾限(SongCatalog.MaxOffsetMs)，先擋在這裡免得白調
            messagebox.showerror("offset 太大", f"offset 只收 ±{MAX_OFFSET_MS:g}ms(遊戲端也會夾)。是不是多打了一個 0?")
            return

        # 一首歌的 K/T 兩列共用同一組顯示值 → 兩列都寫(stbl.save 也會再同步一次)。
        table = stbl.load(TABLE)
        hit = [r for r in table if stem(r["gn"]) == s.stem]
        if not hit:
            messagebox.showerror("找不到這首", f"{s.stem} 不在 song_table.csv 裡。")
            return
        for row in hit:
            row["title"] = self.f_title.get().strip()
            row["artist"] = self.f_artist.get().strip()
            row["bpm"] = bpm                   # 0 = 不覆蓋(顯示回譜面的 BPM)
            row["offsetMs"] = off_ms           # 0 = 不位移音樂
            row["src"] = "manual"              # 標記手改 → build_song_name_overrides 重跑時會保留
        stbl.save(table, TABLE)
        self.reload()
        self.status.config(text=f"已更新 {s.stem}(遊戲下次啟動生效)")

    # ---- 刪歌
    def on_delete(self):
        sel = self.selected()
        if not sel:
            return
        names = "\n".join(f"  {s.stem}  id={s.file_id}  {s.title}" for s in sel[:15])
        more = f"\n  …共 {len(sel)} 首" if len(sel) > 15 else ""
        if not messagebox.askyesno(
                "確定移除?",
                f"會刪掉這些歌的檔案(譜面/主音樂/試聽/舞蹈/封面)並從 song_table.csv 移除:\n\n{names}{more}\n\n"
                f"這動作不可復原(檔案真的會被刪)。要繼續嗎?"):
            return
        by_stem = load_catalog()
        try:
            remove_stems({s.stem for s in sel}, by_stem, dry_run=False, keep_files=False)
        except Exception as ex:
            messagebox.showerror("移除失敗", f"{ex}\n\n{traceback.format_exc()}")
        self.reload()
        self.status.config(text=f"已移除 {len(sel)} 首")

    # ---- 加歌
    def on_add(self, insert_after: bool = False):
        base = None
        if insert_after:
            sel = self.selected()
            if len(sel) != 1:
                messagebox.showinfo("插在哪首之後?", "請先在清單選一首(單選),再按這個鈕。")
                return
            base = sel[0].stem
        AddDialog(self, base)


class AddDialog(tk.Toplevel):
    """加一首歌:選檔 → 自動配空號 → 複製改名 → 更新 song_table.csv。"""

    def __init__(self, app: App, base_stem: Optional[str]):
        super().__init__(app)
        self.app = app
        self.title("新增歌曲")
        self.transient(app)
        self.grab_set()
        self.resizable(False, False)

        self.stems, self.fids = used_slots()
        self.v: Dict[str, tk.StringVar] = {k: tk.StringVar() for k in
                                           ("k", "t", "ogg", "exper", "dps", "icon",
                                            "stem", "fid", "title", "artist", "bpm")}
        # 插在某首之後 → sdom1150_1(字串序剛好落在 sdom1150 隔壁);否則接在最大號後面。
        # (算式不能寫進 f-string:Python 3.12 以前不准 f-string 的表達式裡出現反斜線。)
        nums = [int(m.group(1)) for m in (re.match(r"sdom(\d+)$", s) for s in self.stems) if m]
        default_base = base_stem or "sdom{:04d}".format(max(nums, default=0) + 1)
        self.v["stem"].set(suggest_stem(default_base, self.stems))
        self.v["fid"].set(str(suggest_file_id(self.fids)))

        f = ttk.Frame(self, padding=10)
        f.pack(fill="both")
        r = 0
        for key, label, pat, need in (
                ("k", "K 譜 (.gn)", [("SDO 譜面", "*.gn")], True),
                ("t", "T 譜 (.gn，可空)", [("SDO 譜面", "*.gn")], False),
                ("ogg", "主音樂 (.ogg)", [("Ogg Vorbis", "*.ogg")], True),
                ("exper", "試聽 (.ogg，可空)", [("Ogg Vorbis", "*.ogg")], False),
                ("dps", "舞蹈 (.dps，可空)", [("DPS", "*.dps *.DPS")], False),
                ("icon", "封面 (.png，可空)", [("PNG", "*.png")], False)):
            ttk.Label(f, text=label + ("  *" if need else "")).grid(row=r, column=0, sticky="e", pady=2)
            ttk.Entry(f, textvariable=self.v[key], width=58).grid(row=r, column=1, padx=4)
            ttk.Button(f, text="…", width=3,
                       command=lambda k=key, p=pat: self._pick(k, p)).grid(row=r, column=2)
            r += 1

        box = ttk.LabelFrame(f, text="編號(已自動配了沒人用的空號，可改)", padding=8)
        box.grid(row=r, column=0, columnspan=3, sticky="ew", pady=(10, 4))
        ttk.Label(box, text="詞幹").grid(row=0, column=0, sticky="e")
        ttk.Entry(box, textvariable=self.v["stem"], width=16).grid(row=0, column=1, padx=(4, 4))
        ttk.Label(box, text="→ 譜面 sdomNNNN{K,T}.gn + 主音樂 sdomNNNN.ogg").grid(row=0, column=2, sticky="w")
        ttk.Label(box, text="fileId").grid(row=1, column=0, sticky="e", pady=(4, 0))
        ttk.Entry(box, textvariable=self.v["fid"], width=16).grid(row=1, column=1, padx=(4, 4), pady=(4, 0))
        ttk.Label(box, text="→ 試聽 exper/<id>.ogg + 舞蹈 DANCE/<id>.DPS + 封面 ICONS/<id>.PNG").grid(
            row=1, column=2, sticky="w", pady=(4, 0))
        if base_stem:
            ttk.Label(box, foreground="#06c",
                      text=f"插在 {base_stem} 隔壁:字串序上 {self.v['stem'].get()} 就排在它旁邊，既有檔案完全不動。").grid(
                row=2, column=0, columnspan=3, sticky="w", pady=(6, 0))
        r += 1

        box2 = ttk.LabelFrame(f, text="歌名(可空 → 用 .gn 內嵌的名字)", padding=8)
        box2.grid(row=r, column=0, columnspan=3, sticky="ew", pady=4)
        for i, (key, label, w) in enumerate((("title", "歌名", 30), ("artist", "歌手", 18), ("bpm", "BPM", 8))):
            ttk.Label(box2, text=label).grid(row=0, column=i * 2, sticky="e")
            ttk.Entry(box2, textvariable=self.v[key], width=w).grid(row=0, column=i * 2 + 1, padx=(4, 12))
        r += 1

        self.msg = ttk.Label(f, text="", foreground="#c00", wraplength=620, justify="left")
        self.msg.grid(row=r, column=0, columnspan=3, sticky="w", pady=(6, 4))
        r += 1
        bar = ttk.Frame(f)
        bar.grid(row=r, column=0, columnspan=3, sticky="e")
        ttk.Button(bar, text="取消", command=self.destroy).pack(side="right", padx=4)
        ttk.Button(bar, text="加入", command=self.on_ok).pack(side="right")

    def _pick(self, key: str, patterns):
        p = filedialog.askopenfilename(parent=self, filetypes=patterns + [("所有檔案", "*.*")])
        if not p:
            return
        self.v[key].set(p)
        # 選了 K 譜就順手猜同資料夾的 T 譜 / 同名 .ogg，少點點幾下
        if key == "k":
            src = Path(p)
            t = src.with_name(re.sub(r"[kK](\.gn)$", r"T\1", src.name))
            if t != src and t.is_file() and not self.v["t"].get():
                self.v["t"].set(str(t))
            ogg = src.with_name(re.sub(r"[ktKT]\.gn$", ".ogg", src.name))
            if ogg.is_file() and not self.v["ogg"].get():
                self.v["ogg"].set(str(ogg))

    def on_ok(self):
        try:
            self._do_add()
        except Exception as ex:
            self.msg.config(text=f"失敗:{ex}")
            traceback.print_exc()

    def _do_add(self):
        def path_of(key: str) -> Optional[Path]:
            s = self.v[key].get().strip()
            return Path(s) if s else None

        st = self.v["stem"].get().strip().lower()
        fid = int(self.v["fid"].get().strip())
        log = add_song(k=Path(self.v["k"].get().strip()), t=path_of("t"), ogg=path_of("ogg"),
                       exper=path_of("exper"), dps=path_of("dps"), icon=path_of("icon"),
                       stem_=st, file_id=fid,
                       title=self.v["title"].get().strip(), artist=self.v["artist"].get().strip(),
                       bpm=self.v["bpm"].get())
        print("\n".join(log))
        self.destroy()
        self.app.reload()
        self.app.status.config(text=f"已加入 {st}(fileId {fid})。遊戲下次啟動就看得到;打包版要重跑 package_build.ps1。")


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    App().mainloop()
