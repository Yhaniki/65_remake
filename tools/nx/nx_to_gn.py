# -*- coding: utf-8 -*-
"""
nx_to_gn — 把 [NX]Patch 的 .nx 譜面整包轉成標準 SDOM .gn，並留下一張金鑰表。

為什麼要轉：
  .nx 就是一般的 SDOM .gn 容器，只是 note 資料多包了一層 patcher 的**無金鑰**固定變換
  （見 crack_nx.py / NX_FORMAT.md §2）。剝掉那層之後，檔案結構就跟遊戲原本的 .gn 一模一樣，
  重製版的 GnChart 直接讀得動 —— 前提是拿得到那把 LCG seed。

  而 [NX] 這包**每首歌的 seed 都不一樣**（實測 199 檔 199 把），跟離線單機版整個語料只有
  ~150 把共用 seed 的情形完全不同：runtime 的共用 seed 池救不了它。所以 seed 必須先算好存表，
  這也是本工具第二個產物的意義 —— 「下次再開可以直接知道 key」。

輸出（預設就寫在 .nx 旁邊，原始 .nx 一律保留不動）：
  <music>/<stem>.gn        標準 SDOM .gn（456B 資源名前綴 + 300B 明文表頭 + LCG 密文）
  <music>/gn_keytable.json 金鑰表，schema 與 tools/gn_keytable.py 產的 gn-keytable/1 相同
  <music>/sdo_pack.tsv     歌單 sidecar（UTF-8）：seed + 表頭數值 + 歌名/歌手 + 音樂/封面/試聽相對路徑
                           —— 遊戲端 Sdo.Osu.SdoPackIndex 讀這張表，就能把整包當外部歌曲庫載入

表頭的一個坑（決定了轉檔怎麼寫）：
  容器裡有**兩份** 300 bytes 表頭：offset 456 的明文那份，和密文區解開後的「重複表頭」。
  兩份在 [NX] 包裡**不一致** —— 明文那份被 patch 改成英文歌名，而且 address_end(+296) 是垃圾值；
  密文那份才是引擎真正拿去解析的（GnChart.ParseStepFile 讀的是解密後的 body）。
  重製版的 GnChart 又要求兩份**逐位元組相同**才認定解密成功，所以轉檔時把解密後那份（權威、
  address_end 正確）寫回明文槽。英文歌名不會掉 —— 它被寫進 sdo_pack.tsv 的 title/artist 欄
  （來源是 SongList.dat，跟遊戲畫面顯示的一致）。

用法：
    python tools/nx/nx_to_gn.py "H:/sdo/260712 SDO CiBMall [NX] Patch 3.0 Official"
    python tools/nx/nx_to_gn.py "<pack>/patch music" --out out/ --dry-run
"""
from __future__ import annotations

import argparse
import json
import os
import struct
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))   # tools/ → gn_keytable
import gn_keytable as gk                                          # noqa: E402
from crack_nx import undo_outer                                   # noqa: E402

STEP_HEADER = 300
PACK_TSV = "sdo_pack.tsv"
KEYTABLE_JSON = "gn_keytable.json"
PACK_SCHEMA = "#sdo-pack/1"
COLUMNS = ("gn", "seed", "fileId", "bpm", "lvE", "lvN", "lvH", "notesE", "notesN", "notesH",
           "durE", "durN", "durH", "audio", "cd", "preview", "dps", "title", "artist")

AUDIO_EXT = (".ogg", ".mp3", ".wav")
ICON_EXT = (".png", ".PNG", ".dds", ".DDS", ".jpg")
DPS_EXT = (".DPS", ".dps")

# 封面（CD 圖）候選目錄，相對於 music 目錄。patch 包是要解壓進遊戲主目錄的，所以裝好之後
# ICONS 會跟本體的合併；這裡把「patch 自帶」與「已裝進遊戲」兩種擺法都找過。
ICON_DIRS = (
    "../patch Datas/UI/MUSIC/ICONS",
    "../Datas/UI/MUSIC/ICONS",
    "../DATA/UI/MUSIC/ICONS",
    "../DatasSDO/UI/MUSIC/ICONS",
    "ICONS",
)

# 編舞 .DPS（有的話遊戲就跳原版的舞，不必自己生一支）。
DANCE_DIRS = (
    "../patch Datas/DANCE",
    "../Datas/DANCE",
    "../DATA/DANCE",
    "../DatasSDO/DANCE",
    "DANCE",
)


def _utf8_stdout():
    out = getattr(sys, "stdout", None)
    if out is not None and hasattr(out, "reconfigure"):
        try:
            out.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass


# ---------------------------------------------------------------------------
# 容器 / 表頭
# ---------------------------------------------------------------------------
def container_header_offset(raw: bytes, scan_max: int = 0x4000) -> Optional[int]:
    """明文 StepFile 表頭在 .nx 內的位移（[NX] 包恆為 456）。

    刻意**不**檢查 address_end —— 容器那份表頭的 +296 是垃圾值（見模組說明），
    用 gn_keytable.find_sdom_gn_inner_offset 會整包 199 檔全部找不到。
    """
    n = len(raw)
    if n < STEP_HEADER + 8:
        return None
    for off in range(0, min(scan_max, n - STEP_HEADER)):
        if raw[off + 4:off + 8] not in (b"gn\x00\x00", b"GN\x00\x00"):
            continue
        ae, an, ah, _ = struct.unpack_from("<4I", raw, off + 284)
        if ae != 300 or not (300 <= an <= ah <= n):
            continue
        return off
    return None


def parse_header(h: bytes) -> Dict[str, Any]:
    """300 bytes StepFile 表頭 → 數值欄位（見 docs/reverse-engineering/SDOM_STEPFILE_HEADER.md）。"""
    bpm = struct.unpack_from("<f", h, 16)[0]
    return {
        "fileId": struct.unpack_from("<I", h, 0)[0],
        "bpm": round(float(bpm), 4) if bpm == bpm and abs(bpm) < 1e6 else 0.0,
        "levels": list(struct.unpack_from("<3h", h, 20)),
        "notes": list(struct.unpack_from("<3I", h, 40)),
        "durations": list(struct.unpack_from("<3I", h, 272)),
        "addresses": list(struct.unpack_from("<4I", h, 284)),
    }


def recover_seed(header: bytes, enc: bytes, known: List[int]) -> Optional[int]:
    """LCG seed（gn_keytable 慣例：state 尚未乘 M 的那個）。先試 known 再暴力還原。

    驗證條件刻意只看「解出來是不是合法 StepFile」，不要求跟容器表頭逐位元組相同 ——
    容器表頭本來就跟密文裡那份不一樣（歌名被 patch 改過、address_end 是垃圾）。
    """
    def ok(seed: int) -> bool:
        d = gk.lcg_transform(seed, enc[:STEP_HEADER], encrypt=False)
        if d[4:8] not in (b"gn\x00\x00", b"GN\x00\x00"):
            return False
        ae, an, ah, aend = struct.unpack_from("<4I", d, 284)
        return ae == 300 and 300 <= an <= ah <= aend <= len(enc)

    for s in known:
        if ok(s):
            return s
    for c in sorted(gk.recover_sdom_seeds_np(header, enc)):   # 256 個等價 seed，取最小者當代表
        if ok(c):
            return c
    return None


def convert(raw: bytes, known: List[int]) -> Tuple[Optional[bytes], Optional[int], Optional[bytes], str]:
    """.nx → (標準 .gn bytes, seed, 權威表頭 300B, 說明)。失敗時第一項為 None。"""
    off = container_header_offset(raw)
    if off is None:
        return None, None, None, "找不到明文表頭"
    header = raw[off:off + STEP_HEADER]
    blob = raw[off + STEP_HEADER:]
    if len(blob) < STEP_HEADER:
        return None, None, None, "密文區太短"

    # patcher 的外層固定變換沒有金鑰；已裝進遊戲的純 .gn 沒有這層，兩種都試。
    for enc in (bytes(undo_outer(blob)), blob):
        seed = recover_seed(header, enc, known)
        if seed is None:
            continue
        authoritative = gk.lcg_transform(seed, enc[:STEP_HEADER], encrypt=False)
        return raw[:off] + authoritative + enc, seed, authoritative, "ok"
    return None, None, None, "還原不出 seed"


# ---------------------------------------------------------------------------
# SongList.dat（歌名/歌手；遊戲畫面顯示的就是這份）
# ---------------------------------------------------------------------------
def read_songlist(path: Path, encoding: str) -> Dict[str, Dict[str, Any]]:
    """gn 檔名(小寫) → {fileId,title,artist}。讀不到就回空 dict（歌名改用檔名代替）。"""
    try:
        data = path.read_bytes()
    except Exception:
        return {}
    if len(data) < 4:
        return {}
    count = struct.unpack_from("<H", data, 0)[0]
    if count <= 0:
        return {}
    sdom = data.find(b"sdom")
    head = sdom if 0 < sdom < 64 else 2
    rec = (len(data) - head) // count
    if rec not in (752, 756):
        return {}
    fo, no = (452, 560) if rec == 752 else (456, 564)

    def s(d: bytes, o: int, ln: int) -> str:
        return d[o:o + ln].split(b"\x00", 1)[0].decode(encoding, "replace").strip()

    out: Dict[str, Dict[str, Any]] = {}
    for i in range(count):
        d = data[head + i * rec: head + (i + 1) * rec]
        if len(d) < rec:
            break
        gn = s(d, 0, 32).lower()
        if not gn:
            continue
        out[gn] = {"fileId": struct.unpack_from("<I", d, fo)[0],
                   "title": s(d, no, 64), "artist": s(d, no + 64, 32)}
    return out


# ---------------------------------------------------------------------------
# 資源解析（音樂 / 封面 / 試聽）
# ---------------------------------------------------------------------------
def _index(dir_path: Path) -> Dict[str, str]:
    """小寫檔名 → 實際檔名（Windows 不分大小寫，但 TSV 要寫真實檔名）。"""
    try:
        return {f.name.lower(): f.name for f in dir_path.iterdir() if f.is_file()}
    except Exception:
        return {}


def audio_kind(path: Path) -> str:
    """看檔頭判斷音檔真正的格式（副檔名會騙人）。回 'mp3'/'ogg'/'wav'/''。"""
    try:
        with path.open("rb") as f:
            h = f.read(12)
    except Exception:
        return ""
    if h[:4] == b"OggS":
        return "ogg"
    if h[:4] == b"RIFF" and h[8:12] == b"WAVE":
        return "wav"
    if h[:3] == b"ID3":
        return "mp3"
    if len(h) > 1 and h[0] == 0xFF and (h[1] & 0xE0) == 0xE0 and (h[1] & 0x06) != 0:
        return "mp3"
    return ""


def audio_name(music_index: Dict[str, str], gn_stem: str) -> str:
    base = gn_stem[:-1] if gn_stem[-1:] in "KTkt" else gn_stem   # sdom0040K → sdom0040
    for ext in AUDIO_EXT:
        hit = music_index.get((base + ext).lower())
        if hit:
            return hit
    return ""


def rel(from_dir: Path, target: Path) -> str:
    """sidecar 裡的路徑：包內資源寫相對（整包搬家還能用），包外的寫絕對。

    邊界拿「往上爬幾層」判斷：`../patch Datas/...` 還在同一包裡；補進來的封面/編舞常常在
    遊戲本體那棵樹，相對路徑會變成一長串 `../../../../..`，只要 Songs 資料夾一搬就全斷 ——
    那種寫絕對路徑反而穩。跨磁碟機時 relpath 直接失敗，也走絕對。
    """
    try:
        r = os.path.relpath(target, from_dir).replace("\\", "/")
        if r.count("../") <= 2:
            return r
    except Exception:
        pass
    return str(target).replace("\\", "/")


def find_by_id(dirs: List[Path], file_id: int, exts) -> Optional[Path]:
    """<dir>/<fileId><ext> —— 封面/編舞都是用歌曲編號命名的。"""
    for d in dirs:
        for ext in exts:
            p = d / f"{file_id}{ext}"
            if p.is_file():
                return p
    return None


def to_png(icon: Path, cd_dir: Path, file_id: int) -> Path:
    """封面統一成 PNG，回傳實際要寫進 sidecar 的路徑。

    官方 ICONS 有 92% 是 **.dds**（這包 146 張裡 134 張，全是未壓縮 A8R8G8B8 237×237）。
    遊戲端的共用圖片載入器只吃 PNG/JPG/BMP，而繞去 DdsLoader 又有兩個坑：解碼器最後都
    `Apply(..., makeNoLongerReadable: true)`（拿不回像素、翻不了列），且 DDS 是「第一列在上」
    而 Unity 貼圖第一列在下（3D 的 UV 會自動抵銷，UI sprite 不會）。離線轉成 PNG 一次解決兩件事，
    順便把 224 KB 的未壓縮圖壓成 ~30 KB。

    已經是 PNG/JPG 的原地用，不搬不轉。轉檔失敗（沒有 Pillow）就退回指向 .dds，
    讓 runtime 的後備解碼器去處理。
    """
    if icon.suffix.lower() != ".dds":
        return icon
    out = cd_dir / f"{file_id}.png"
    if out.is_file() and out.stat().st_mtime >= icon.stat().st_mtime:
        return out
    try:
        from PIL import Image
        with Image.open(icon) as im:
            cd_dir.mkdir(parents=True, exist_ok=True)
            im.convert("RGBA").save(out, "PNG", optimize=True)
        return out
    except Exception:
        return icon


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
def resolve_music_dir(root: Path) -> Optional[Path]:
    """使用者可以指到 pack 根目錄或直接指到 music 目錄。"""
    for cand in (root, root / "patch music", root / "music"):
        if cand.is_dir() and any(f.suffix.lower() in (".nx", ".gn") for f in cand.iterdir() if f.is_file()):
            return cand
    return None


def load_known_seeds(path: Path) -> Tuple[Dict[str, int], List[int]]:
    """既有金鑰表 → (gn→seed, 去重 seed 清單)。有這張表，重跑就不必再暴力還原。"""
    by_gn: Dict[str, int] = {}
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return by_gn, []
    for s in doc.get("songs", []):
        gn, seed = s.get("gn"), s.get("seed")
        if gn and isinstance(seed, int):
            by_gn[gn.lower()] = seed
    seen, order = set(), []
    for v in by_gn.values():
        if v not in seen:
            seen.add(v)
            order.append(v)
    return by_gn, order


def tsv_cell(v: Any) -> str:
    return str(v).replace("\t", " ").replace("\r", " ").replace("\n", " ")


def main() -> int:
    _utf8_stdout()
    ap = argparse.ArgumentParser(description="把 [NX]Patch 的 .nx 譜面轉成標準 .gn 並產生金鑰表")
    ap.add_argument("root", help="pack 根目錄或 music 目錄")
    ap.add_argument("--out", help="輸出目錄（預設就寫在 .nx 旁邊）")
    ap.add_argument("--icons", action="append", default=[], help="額外的封面(ICONS)目錄，可重複指定"
                                                                "（patch 只帶部分封面，其餘在遊戲本體的 DATA/UI/MUSIC/ICONS）")
    ap.add_argument("--dance", action="append", default=[], help="額外的編舞(DANCE)目錄，可重複指定")
    ap.add_argument("--encoding", default="gb18030", help="SongList.dat 字串編碼（大陸版 gb18030 / 台版 big5）")
    ap.add_argument("--force", action="store_true", help="已存在的 .gn 也重新轉換覆蓋")
    ap.add_argument("--dry-run", action="store_true", help="只統計，不寫任何檔")
    args = ap.parse_args()

    root = Path(args.root).resolve()
    music = resolve_music_dir(root)
    if music is None:
        print(f"找不到含 .nx/.gn 的 music 目錄：{root}", file=sys.stderr)
        return 1
    out_dir = Path(args.out).resolve() if args.out else music
    nx_files = sorted((f for f in music.iterdir() if f.is_file() and f.suffix.lower() == ".nx"),
                      key=lambda p: p.name.lower())
    if not nx_files:
        print(f"{music} 裡沒有 .nx", file=sys.stderr)
        return 1

    songlist = read_songlist(music / "SongList.dat", args.encoding)
    music_index = _index(music)
    exper_dir = music / "exper"
    exper_index = _index(exper_dir)
    # 包自帶的先找、--icons/--dance 只當補件：patch 特地帶的封面就是這個 pack 該長的樣子，
    # 遊戲本體那套是拿來補 patch 沒帶的那些，不該反過來蓋掉。
    icon_dirs = [(music / d).resolve() for d in ICON_DIRS] + [Path(d).resolve() for d in args.icons]
    icon_dirs = [d for d in icon_dirs if d.is_dir()]
    dance_dirs = [(music / d).resolve() for d in DANCE_DIRS] + [Path(d).resolve() for d in args.dance]
    dance_dirs = [d for d in dance_dirs if d.is_dir()]

    known_by_gn, known_seeds = load_known_seeds(out_dir / KEYTABLE_JSON)
    print(f"music={music}\nout={out_dir}\n.nx={len(nx_files)}  SongList={len(songlist)} 筆  "
          f"ICONS={len(icon_dirs)} 個目錄  DANCE={len(dance_dirs)} 個目錄  已知金鑰={len(known_by_gn)}")

    rows: List[Dict[str, Any]] = []
    songs: List[Dict[str, Any]] = []
    failed: List[Tuple[str, str]] = []
    mislabelled: List[str] = []
    stat = dict(converted=0, reused=0, cd_png=0, mislabelled=0, no_audio=0, no_cd=0, no_preview=0, no_dps=0, no_songlist=0)
    t0 = time.time()

    for i, f in enumerate(nx_files, 1):
        stem = f.stem                      # sdom0040K
        gn_name = stem + ".gn"
        gn_path = out_dir / gn_name
        raw = f.read_bytes()

        pool = ([known_by_gn[gn_name.lower()]] if gn_name.lower() in known_by_gn else []) + known_seeds
        conv, seed, header, why = convert(raw, pool)
        if conv is None:
            failed.append((f.name, why))
            continue
        if seed not in known_seeds:
            known_seeds.insert(0, seed)    # 同包偶爾共用 → 先試，省一次暴力還原

        if args.dry_run:
            stat["converted"] += 1
        elif gn_path.exists() and not args.force and gn_path.read_bytes() == conv:
            stat["reused"] += 1
        else:
            gn_path.parent.mkdir(parents=True, exist_ok=True)
            gn_path.write_bytes(conv)
            stat["converted"] += 1

        hd = parse_header(header)
        meta = songlist.get(gn_name.lower())
        if meta is None:
            stat["no_songlist"] += 1
        file_id = (meta or {}).get("fileId") or hd["fileId"]

        audio = audio_name(music_index, stem)
        if not audio:
            stat["no_audio"] += 1
        else:
            # 副檔名 vs 實際內容：這包就有 4 個 Ogg 取名叫 .mp3。遊戲端已經改成看內容派解碼器
            # （Sdo.Osu.AudioFileType），所以照樣播得出來；這裡只是把它列出來讓人知道。
            kind = audio_kind(music / audio)
            if kind and kind != Path(audio).suffix.lower().lstrip("."):
                stat["mislabelled"] += 1
                mislabelled.append(f"{audio}（實際是 {kind}）")
        icon = find_by_id(icon_dirs, file_id, ICON_EXT)
        if icon is None:
            stat["no_cd"] += 1
        elif not args.dry_run:
            was = icon
            icon = to_png(icon, out_dir / "cd", file_id)
            if icon is not was:
                stat["cd_png"] += 1
        dps = find_by_id(dance_dirs, file_id, DPS_EXT)
        if dps is None:
            stat["no_dps"] += 1
        preview = exper_index.get(f"{file_id}.ogg")
        if not preview:
            stat["no_preview"] += 1

        rows.append({
            "gn": gn_name, "seed": seed, "fileId": file_id, "bpm": hd["bpm"],
            "lvE": hd["levels"][0], "lvN": hd["levels"][1], "lvH": hd["levels"][2],
            "notesE": hd["notes"][0], "notesN": hd["notes"][1], "notesH": hd["notes"][2],
            "durE": hd["durations"][0], "durN": hd["durations"][1], "durH": hd["durations"][2],
            "audio": rel(out_dir, music / audio) if audio else "",
            "cd": rel(out_dir, icon) if icon else "",
            "preview": rel(out_dir, exper_dir / preview) if preview else "",
            "dps": rel(out_dir, dps) if dps else "",
            "title": (meta or {}).get("title", "") or stem,
            "artist": (meta or {}).get("artist", ""),
        })
        songs.append({"gn": gn_name.lower(), "enc": "sdom",
                      "mode": stem[-1].upper() if stem[-1:] in "KTkt" else "",
                      "seed": seed, "innerOff": container_header_offset(conv) or 0,
                      "fileId": file_id, "bpm": hd["bpm"], "title": rows[-1]["title"],
                      "size": len(conv), "nx": f.name})
        if i % 50 == 0 or i == len(nx_files):
            print(f"  [{i}/{len(nx_files)}] {time.time() - t0:.1f}s", flush=True)

    if not args.dry_run and rows:
        out_dir.mkdir(parents=True, exist_ok=True)
        (out_dir / KEYTABLE_JSON).write_text(json.dumps({
            "schema": "gn-keytable/1",
            "generatedBy": "tools/nx/nx_to_gn.py",
            "root": music.name,
            "multiplier": "0x3D09",
            "howto": {
                "sdom": "body = lcg_decrypt(seed, raw[innerOff+300:]); stepfileHeader = raw[innerOff:innerOff+300]",
                "lcg": "state=seed; 每步 state=(state*0x3D09)&0xffffffff; k=(state>>16)&0xff; out=in-k (解密)",
                "nx": ".nx = 同結構但密文多一層無金鑰固定變換，本工具已剝除；.gn 可直接用 seed 解",
            },
            "counts": {"total": len(nx_files), "sdom": len(songs), "failed": len(failed)},
            "songs": songs,
        }, ensure_ascii=False, indent=1), encoding="utf-8")

        lines = [PACK_SCHEMA,
                 "# tools/nx/nx_to_gn.py 產生；路徑相對本檔所在目錄。遊戲端由 Sdo.Osu.SdoPackIndex 讀取。",
                 "\t".join(COLUMNS)]
        lines += ["\t".join(tsv_cell(r[c]) for c in COLUMNS) for r in rows]
        (out_dir / PACK_TSV).write_text("\n".join(lines) + "\n", encoding="utf-8")

    dt = time.time() - t0
    print(f"\n{'[DRY-RUN] ' if args.dry_run else ''}轉換 {stat['converted']}／沿用 {stat['reused']}／"
          f"失敗 {len(failed)}（共 {len(nx_files)}）  {dt:.1f}s")
    print(f"  金鑰：{len({r['seed'] for r in rows})} 把不重複 seed")
    print(f"  封面：{stat['cd_png']} 張指向轉出來的 PNG（.dds 遊戲端讀不到，一律離線轉一份）")
    print(f"  缺件：音樂 {stat['no_audio']}／封面 {stat['no_cd']}／試聽 {stat['no_preview']}／"
          f"編舞 {stat['no_dps']}／SongList {stat['no_songlist']}")
    if not args.dry_run and rows:
        print(f"  已寫出：{out_dir / KEYTABLE_JSON}\n            {out_dir / PACK_TSV}")
    if mislabelled:
        print(f"  音檔副檔名名不符實 {len(mislabelled)} 個（遊戲端看內容判斷，照樣播得出來）："
              + "、".join(mislabelled[:6]) + ("…" if len(mislabelled) > 6 else ""))
    if failed:
        print("  失敗清單：", failed[:20])
    return 0 if not failed else 2


if __name__ == "__main__":
    raise SystemExit(main())
