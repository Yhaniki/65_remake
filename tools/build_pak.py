# -*- coding: utf-8 -*-
"""把散裝的 DATA 樹打包成 SDOPAK 分卷。

用法
----
    # 全打（明碼，開發用）
    python tools/build_pak.py --source H:\\65_remake_clean\\DATA --out Build\\Windows\\DATA

    # 出貨（加密）
    python tools/build_pak.py --source ... --out ... --encrypt

    # 只重打一卷（改了 UI 美術，不必動 4 GB 的 AVATAR）
    python tools/build_pak.py --source ... --out ... --only base_core

    # 產 patch 卷：比對既有的 .manifest.json，只放變動/新增，消失的寫成 whiteout
    python tools/build_pak.py --source ... --out ... --patch --id 1

分卷是按「更新頻率」而不是目錄結構切的 —— 改一個 UI 貼圖不該讓玩家重載 4 GB 的 AVATAR。
表在 VOLUMES;規格與理由見 docs/architecture/data-packaging.md §2。

每卷會附一份 <卷名>.manifest.json（路徑 → size/crc），下次做 patch diff 與驗證都靠它。
🔴 manifest 預設寫到 <out>/../pak_manifests，**刻意不在出貨的 DATA 裡** ——
   它是每一條路徑的明文（base_avatar 那份有 5.4 MB），跟著出貨等於索引加密白做。

reserved 目錄（PROFILE / ADDON / CACHE / REPLAY）永遠不進 pak —— 那是玩家可寫的明碼區。
sdopak.PakBuilder 會再擋一次，這裡先過濾只是為了不做白工。
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import sdopak  # noqa: E402


# ---------------------------------------------------------------- 分卷表

#: name → (pakId, [頂層目錄…], compress, crypt_range)
#:
#: MUSIC 一定要 store：mp3/ogg 再壓收益是 0，純燒 CPU 跟打包時間。
#: 音訊只加密前 4 KB（CRYPT_HEADER_ONLY）：播放器直接開會失敗，但串流時只需解前 4 KB，
#: CPU 幾乎免費 —— 整套加密裡 CP 值最高的一招。
VOLUMES: list[dict] = [
    dict(name="base_core", pak_id=10, compress=True, crypt=sdopak.CRYPT_WHOLE,
         dirs=["UI", "EFFECT", "3DEFT", "3DNOTES", "NOTEIMAGE", "ITEM2D", "DAOJU", "EMBLEM", "LOADING"]),
    dict(name="base_avatar", pak_id=11, compress=True, crypt=sdopak.CRYPT_WHOLE,
         dirs=["AVATAR"]),
    dict(name="base_motion", pak_id=12, compress=True, crypt=sdopak.CRYPT_WHOLE,
         dirs=["MOTION", "AUMOTION", "DANCE", "CAMERA"]),
    dict(name="base_scene", pak_id=13, compress=True, crypt=sdopak.CRYPT_WHOLE,
         dirs=["SCENE"]),
    dict(name="base_se", pak_id=14, compress=False, crypt=sdopak.CRYPT_HEADER_ONLY,
         dirs=["SE"]),
    dict(name="music", pak_id=20, compress=False, crypt=sdopak.CRYPT_HEADER_ONLY,
         dirs=["MUSIC"], split_bytes=1024 * 1024 * 1024),   # 每卷約 1 GB
    # 🔴 以下維持散裝（loose=True）—— 都是**玩家會自己放東西進去**的地方。
    #    打包它們等於把那個功能關掉，而且是靜默關掉：玩家把檔案丟進資料夾，遊戲就是看不到。
    #    散裝層的優先權在所有 pak 之上，所以「不打包」在 VFS 那邊零成本。
    dict(name="bgm", pak_id=15, compress=False, crypt=sdopak.CRYPT_HEADER_ONLY,
         dirs=["BGM"], loose=True),
    # MMD 模型在 ADDON/MODEL —— ADDON 是 reserved 目錄，scan() 本來就會跳過，
    # 所以這裡**不需要**任何規則。舊安裝可能還有 <DATA>/MODEL，那個會落到 base_misc
    # 並印警告（dirs 空 → 不會刪散裝樹，見 volume_spec 的說明）。
]

# 音訊怎麼從 pak 裡放出來（**不落地**）
#
# Unity 沒有記憶體 ogg 解碼器：UnityWebRequestMultimedia 只吃 file://。所以自己帶解碼器：
#   ogg → sdovorbis.dll（stb_vorbis 包裝，public domain）
#   wav → WavDecoder.cs（自己 parse RIFF）
#   mp3 → sdomad.dll（libmad，與 StepMania 逐位相同）—— 那條路一行都沒動
# 入口是 MemoryAudio.Load：VFS 位元組 → 看內容判格式 → PCM → AudioClip.Create。
#
# mp3 永遠不會從 pak 讀：官方 MUSIC 是 100% ogg，mp3 只出現在 ADDON/SONG（reserved，永不打包），
# 所以那一整套 gapless/priming 修正完全不受影響。
# 詳見 docs/architecture/data-packaging.md §2.1。

#: 頂層的零星檔案（iteminfo.dat / shop_names.tsv …）跟著 base_core 走。
ROOT_FILES_VOLUME = "base_core"

#: 分卷表沒提到的頂層目錄。有東西進這裡代表 VOLUMES 漏了，要提醒而不是靜默丟掉。
UNASSIGNED_VOLUME = "base_misc"
UNASSIGNED_PAK_ID = 19

#: patch 卷的 pakId 起點 —— 一定要高過所有 base/music，掛載順序與金鑰派生都看它。
PATCH_PAK_ID_BASE = 300


# ---------------------------------------------------------------- 掃描

def scan(source: Path) -> dict[str, Path]:
    """DATA 樹 → {正規化相對路徑: 絕對路徑}。reserved 目錄直接跳過。"""
    out: dict[str, Path] = {}
    skipped_reserved = 0
    for p in source.rglob("*"):
        if not p.is_file():
            continue
        rel = sdopak.normalize(str(p.relative_to(source)))
        if not rel:
            continue
        if sdopak.is_reserved(rel):
            skipped_reserved += 1
            continue
        out[rel] = p
    if skipped_reserved:
        print(f"[pak] 跳過 reserved 目錄裡的 {skipped_reserved} 個檔（PROFILE/ADDON/CACHE/REPLAY）")
    return out


def top_dir(rel: str) -> str:
    return rel.split("/", 1)[0] if "/" in rel else ""


def assign(files: dict[str, Path]) -> dict[str, list[str]]:
    """相對路徑 → 卷名。回傳 {卷名: [路徑…]}。"""
    by_dir: dict[str, str] = {}
    for v in VOLUMES:
        for d in v["dirs"]:
            by_dir[d.upper()] = v["name"]

    groups: dict[str, list[str]] = {}
    unassigned_dirs: set[str] = set()
    for rel in files:
        td = top_dir(rel)
        if td == "":
            vol = ROOT_FILES_VOLUME                     # DATA 根的零星檔
        elif td.upper() in by_dir:
            vol = by_dir[td.upper()]
        else:
            vol = UNASSIGNED_VOLUME
            unassigned_dirs.add(td)
        groups.setdefault(vol, []).append(rel)

    if unassigned_dirs:
        # 靜默丟掉會變成「遊戲少了某些資產」那種很難查的問題 —— 一定要講出來。
        print(f"[pak] ⚠️ 分卷表沒提到這些頂層目錄，收進 {UNASSIGNED_VOLUME}: "
              + ", ".join(sorted(unassigned_dirs)))
    return groups


def split_by_size(paths: list[str], files: dict[str, Path], limit: int) -> list[list[str]]:
    """依累計大小切成多卷（MUSIC 用）。先排序才 deterministic。"""
    chunks: list[list[str]] = [[]]
    total = 0
    for rel in sorted(paths):
        size = files[rel].stat().st_size
        if chunks[-1] and total + size > limit:
            chunks.append([])
            total = 0
        chunks[-1].append(rel)
        total += size
    return chunks


# ---------------------------------------------------------------- 打包

def volume_spec(name: str) -> dict:
    for v in VOLUMES:
        if v["name"] == name:
            return v
    # 沒對到分卷表 → 收進 base_misc。dirs 刻意留空:packed_dirs.json 因此不會列到它們，
    # package_build 也就**不會刪掉那些散裝目錄**。那是保險，不是疏漏 ——
    # 分卷表漏掉的東西通常正是「玩家會自己放檔案進去」的地方（MODEL 就是這樣被發現的），
    # 打包了還把散裝樹刪掉，那個功能就靜默失效了。看到 base_misc 出現就去補 VOLUMES。
    return dict(name=UNASSIGNED_VOLUME, pak_id=UNASSIGNED_PAK_ID,
                compress=True, crypt=sdopak.CRYPT_WHOLE, dirs=[])


def _hms(sec: float) -> str:
    sec = int(sec)
    return f"{sec // 60}m{sec % 60:02d}s" if sec >= 60 else f"{sec}s"


class Progress:
    """一行原地更新的進度列。base_avatar 有 4 GB / 幾萬個檔,跑好幾分鐘 —— 沒有這個就是一片死寂。

    導向檔案時(build_windows.ps1 的 log、CI)不能噴幾萬個 \\r:偵測到不是終端機就改成
    每 15 秒印一行普通的。
    """

    def __init__(self, label: str, total_files: int, total_bytes: int):
        self.label, self.total_files, self.total_bytes = label, total_files, max(total_bytes, 1)
        self.tty = sys.stdout.isatty()
        self.interval = 0.25 if self.tty else 15.0
        self.t0 = time.time()
        self.last = 0.0
        self.width = 0

    def __call__(self, done: int, done_bytes: int) -> None:
        now = time.time()
        if now - self.last < self.interval and done != self.total_files:
            return
        self.last = now
        el = now - self.t0
        rate = done_bytes / el if el > 0.1 else 0.0
        eta = (self.total_bytes - done_bytes) / rate if rate > 0 else 0.0
        line = (f"[pak] {self.label:<14} {done:>7}/{self.total_files} 檔 "
                f"{done_bytes / 1048576:>8.1f}/{self.total_bytes / 1048576:.1f} MB "
                f"{rate / 1048576:>6.1f} MB/s"
                + (f"  剩 {_hms(eta)}" if rate > 0 else ""))
        if self.tty:
            # 補空白蓋掉上一行的殘影(這一行比較短時)
            sys.stdout.write("\r" + line.ljust(self.width))
            sys.stdout.flush()
            self.width = max(self.width, len(line))
        else:
            print(line, flush=True)

    def clear(self) -> None:
        """把進度列擦掉,讓後面那行總結乾乾淨淨地印出來。"""
        if self.tty and self.width:
            sys.stdout.write("\r" + " " * self.width + "\r")
            sys.stdout.flush()


def build_volume(out_dir: Path, manifest_dir: Path, name: str, pak_id: int, paths: list[str],
                 files: dict[str, Path], spec: dict, encrypt: bool) -> dict:
    b = sdopak.PakBuilder(pak_id, encrypt=encrypt)
    for rel in sorted(paths):
        b.add_file(rel, files[rel], compress=spec["compress"], crypt_range=spec["crypt"])

    pak_path = out_dir / f"{name}.pak"
    src_bytes = sum(files[r].stat().st_size for r in paths)     # 進度列與 manifest 共用,只 stat 一輪
    bar = Progress(name, len(paths), src_bytes)
    t0 = time.time()
    manifest = b.write(pak_path, progress=bar)
    dt = time.time() - t0
    bar.clear()

    manifest["source_bytes"] = src_bytes
    manifest_dir.mkdir(parents=True, exist_ok=True)
    (manifest_dir / f"{name}.manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=1, sort_keys=True), encoding="utf-8")

    size = pak_path.stat().st_size
    src = manifest["source_bytes"]
    ratio = (size / src * 100) if src else 100
    print(f"[pak] {name:<14} {len(paths):>7} 檔  {src/1048576:>8.1f} MB → "
          f"{size/1048576:>8.1f} MB ({ratio:.0f}%)  {dt:.1f}s")
    return manifest


def build_all(source: Path, out_dir: Path, manifest_dir: Path, encrypt: bool, only: str | None) -> None:
    files = scan(source)
    print(f"[pak] 來源 {source}：{len(files)} 個檔")
    groups = assign(files)
    out_dir.mkdir(parents=True, exist_ok=True)

    packed_dirs: list[str] = []
    loose_dirs: list[str] = []

    for name, paths in sorted(groups.items()):
        spec = volume_spec(name)

        if spec.get("loose"):
            # 這一群維持散裝 —— 散裝層的優先權在所有 pak 之上，遊戲照樣讀得到。
            loose_dirs.extend(spec["dirs"])
            n = len(paths)
            mb = sum(files[r].stat().st_size for r in paths) / 1048576
            print(f"[pak] {name:<14} {n:>7} 檔  {mb:>8.1f} MB → 維持散裝（{', '.join(spec['dirs'])}）")
            continue

        packed_dirs.extend(spec["dirs"])
        limit = spec.get("split_bytes")
        if limit:
            for i, chunk in enumerate(split_by_size(paths, files, limit)):
                vol_name = f"{name}_{i:03d}"
                if only and only not in (name, vol_name):
                    continue
                build_volume(out_dir, manifest_dir, vol_name, spec["pak_id"] + i, chunk, files, spec, encrypt)
        else:
            if only and only != name:
                continue
            build_volume(out_dir, manifest_dir, name, spec["pak_id"], paths, files, spec, encrypt)

    # package_build.ps1 靠這份決定「打包後可以刪掉哪些散裝目錄」—— 讓兩邊不會各自維護一份清單而漂移。
    manifest_dir.mkdir(parents=True, exist_ok=True)
    (manifest_dir / "packed_dirs.json").write_text(
        json.dumps({"packed": sorted(set(packed_dirs)), "loose": sorted(set(loose_dirs))},
                   ensure_ascii=False, indent=1),
        encoding="utf-8")


# ---------------------------------------------------------------- patch

def load_manifests(manifest_dir: Path) -> dict[str, dict]:
    """既有卷的 manifest 併成一張 {路徑: {size, crc}}。patch 卷本身也算進去 ——
    patch 是疊加的，第二個 patch 要跟「base + 第一個 patch」比。"""
    merged: dict[str, dict] = {}
    for m in sorted(manifest_dir.glob("*.manifest.json")):
        try:
            data = json.loads(m.read_text(encoding="utf-8"))
        except Exception as e:
            print(f"[pak] ⚠️ 讀不了 {m.name}: {e}")
            continue
        merged.update(data.get("files", {}))
        for w in data.get("whiteouts", []):
            merged.pop(w, None)
    return merged


def build_patch(source: Path, out_dir: Path, manifest_dir: Path, encrypt: bool, patch_id: int) -> None:
    old = load_manifests(manifest_dir)
    if not old:
        raise SystemExit(f"[pak] {manifest_dir} 底下沒有任何 .manifest.json —— 先做一次完整打包")

    files = scan(source)
    import zlib

    # 🔴 loose 的卷（BGM）跟完整打包時一樣要跳過。它們從來不在任何 manifest 裡，所以每一個檔都會被
    #    判成「新增」—— 不擋的話每產一次 patch 就白白多背一份 BGM（遊戲還是讀散裝的那份，散裝層的
    #    優先權在所有 pak 之上，所以那份 patch 裡的複本連讀都不會被讀到）。
    vol_of = {rel: vol for vol, rels in assign(files).items() for rel in rels}
    files = {rel: p for rel, p in files.items() if not volume_spec(vol_of[rel]).get("loose")}

    changed: list[str] = []
    for rel, p in sorted(files.items()):
        prev = old.get(rel)
        size = p.stat().st_size
        if prev is None or prev.get("size") != size:
            changed.append(rel)
            continue
        # 大小一樣才需要算 CRC（省掉絕大多數的讀檔）
        if prev.get("crc") != (zlib.crc32(p.read_bytes()) & 0xFFFFFFFF):
            changed.append(rel)

    removed = sorted(set(old) - set(files))

    if not changed and not removed:
        print("[pak] 沒有任何變動 —— 不產 patch 卷")
        return

    name = f"patch_{patch_id:03d}"
    # patch 卷的 pakId 一定要落在 base/music 之上 —— 掛載順序與金鑰派生都看它。
    b = sdopak.PakBuilder(PATCH_PAK_ID_BASE + patch_id, encrypt=encrypt)

    # 每個檔仍然沿用它原本那一卷的策略（音訊還是 store + 只加密表頭）。
    for rel in changed:
        spec = volume_spec(vol_of.get(rel, UNASSIGNED_VOLUME))
        b.add_file(rel, files[rel], compress=spec["compress"], crypt_range=spec["crypt"])
    for rel in removed:
        b.add_whiteout(rel)

    src_bytes = sum(files[r].stat().st_size for r in changed)
    bar = Progress(name, len(changed) + len(removed), src_bytes)
    manifest = b.write(out_dir / f"{name}.pak", progress=bar)
    bar.clear()
    manifest["source_bytes"] = src_bytes
    manifest_dir.mkdir(parents=True, exist_ok=True)
    (manifest_dir / f"{name}.manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=1, sort_keys=True), encoding="utf-8")

    size = (out_dir / f"{name}.pak").stat().st_size
    print(f"[pak] {name}: {len(changed)} 變動/新增 + {len(removed)} 刪除 → {size/1048576:.1f} MB")


# ---------------------------------------------------------------- CLI

def main() -> int:
    ap = argparse.ArgumentParser(description="把散裝 DATA 樹打包成 SDOPAK 分卷")
    ap.add_argument("--source", required=True, type=Path, help="散裝 DATA 樹（例：H:\\65_remake_clean\\DATA）")
    ap.add_argument("--out", required=True, type=Path, help="輸出目錄（例：Build\\Windows\\DATA）")
    ap.add_argument("--encrypt", action="store_true", help="加密（出貨用；預設明碼，開發好查）")
    ap.add_argument("--only", help="只打這一卷（例：base_core / music_000）")
    ap.add_argument("--manifest-dir", type=Path, default=None,
                    help="manifest 輸出目錄（預設 <out>/../pak_manifests）。"
                         "🔴 絕不能設在出貨的 DATA 裡 —— manifest 是**每一條路徑的明文**，"
                         "跟著出貨等於索引加密白做。")
    ap.add_argument("--patch", action="store_true", help="產 patch 卷（比對 manifest 目錄裡既有的）")
    ap.add_argument("--id", type=int, default=1, help="patch 卷編號（預設 1）")
    args = ap.parse_args()

    if not args.source.is_dir():
        raise SystemExit(f"[pak] 找不到來源目錄: {args.source}")

    manifest_dir = args.manifest_dir or (args.out.parent / "pak_manifests")
    if manifest_dir.resolve() == args.out.resolve() or args.out.resolve() in manifest_dir.resolve().parents:
        raise SystemExit(
            f"[pak] manifest 目錄不能在 --out 底下（{manifest_dir}）—— manifest 是每一條路徑的明文，"
            "跟著出貨等於索引加密白做")
    print(f"[pak] manifest → {manifest_dir}")

    t0 = time.time()
    if args.patch:
        build_patch(args.source, args.out, manifest_dir, args.encrypt, args.id)
    else:
        build_all(args.source, args.out, manifest_dir, args.encrypt, args.only)
    print(f"[pak] 完成，共 {time.time() - t0:.1f}s"
          + ("（已加密）" if args.encrypt else "（明碼）"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
