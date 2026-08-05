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
    # BGM 刻意維持散裝：那是玩家最可能想自己換掉的東西，散裝資料夾丟進去就生效。
    # 散裝層的優先權在所有 pak 之上，所以「不打包」在 VFS 那邊零成本。
    dict(name="bgm", pak_id=15, compress=False, crypt=sdopak.CRYPT_HEADER_ONLY,
         dirs=["BGM"], loose=True),
]

# 音訊怎麼從 pak 裡放出來
#
# Unity 沒有記憶體 ogg 解碼器：UnityWebRequestMultimedia 只吃 file://，Mp3Decoder.Decode 吃的是路徑。
# 所以音訊條目在要播的當下由 VfsFile.MaterialiseRealPath 解出來寫到 DATA/CACHE/AUDIO/ 再交給既有的
# 載入路徑 —— 對 wav/ogg/mp3 一致，也不必動到 gapless 對拍與試聽那些很敏感的程式碼。
#
# 取捨：解出來的檔在 CACHE 裡是明碼。這讓「不能整包拷走直接用」弱一點，但只弱在玩家真的播放過的
# 那些檔，而且 CACHE 本來就是可刪的。詳見 docs/architecture/data-packaging.md §2.1。

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
    return dict(name=UNASSIGNED_VOLUME, pak_id=UNASSIGNED_PAK_ID,
                compress=True, crypt=sdopak.CRYPT_WHOLE, dirs=[])


def build_volume(out_dir: Path, manifest_dir: Path, name: str, pak_id: int, paths: list[str],
                 files: dict[str, Path], spec: dict, encrypt: bool) -> dict:
    b = sdopak.PakBuilder(pak_id, encrypt=encrypt)
    for rel in sorted(paths):
        b.add_file(rel, files[rel], compress=spec["compress"], crypt_range=spec["crypt"])

    pak_path = out_dir / f"{name}.pak"
    t0 = time.time()
    manifest = b.write(pak_path)
    dt = time.time() - t0

    manifest["source_bytes"] = sum(files[r].stat().st_size for r in paths)
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
    vol_of = {rel: vol for vol, rels in assign(files).items() for rel in rels}
    for rel in changed:
        spec = volume_spec(vol_of.get(rel, UNASSIGNED_VOLUME))
        b.add_file(rel, files[rel], compress=spec["compress"], crypt_range=spec["crypt"])
    for rel in removed:
        b.add_whiteout(rel)

    manifest = b.write(out_dir / f"{name}.pak")
    manifest["source_bytes"] = sum(files[r].stat().st_size for r in changed)
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
