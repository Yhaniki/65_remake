# -*- coding: utf-8 -*-
"""
把炸彈功能要用的 StepMania 素材裝進打包來源樹（assets/sdox_offline/），
之後 tools/package_build.ps1 會自動鏡射進 build 的 DATA/，不用改打包腳本。

裝兩個檔：
  <noteskin>/Fallback Tap Explosion Dim HitMine.png → Extracted/NOTEIMAGE/BOMB_EXPLODE.png  (引爆特效圖)
  <theme>/Sounds/Player mine.ogg                    → SE/player_mine.wav                     (引爆音;需 ffmpeg)

為什麼需要這支：這兩個檔是**外部 StepMania 來源**，而 assets/ 整棵被 .gitignore 掉
（專案所有遊戲資料都不進版控），所以 clone 之後要重跑這支才會有。

用法（預設路徑就是本專案用的那份 StepMania）：
    python tools/nx/install_bomb_assets.py
    python tools/nx/install_bomb_assets.py --sm "D:/StepMania做譜" --repo .
"""
from __future__ import annotations
import argparse, shutil, subprocess, sys
from pathlib import Path

DEF_SM = r"D:/StepMania做譜"
THEME = "Themes/CyberiaStyle 6 -consciousness to cyber-/Sounds/Player mine.ogg"
SKIN = "NoteSkins/common/default/Fallback Tap Explosion Dim HitMine.png"
FFMPEG_FALLBACK = r"H:/bms/bak/tools/dist/ffmpeg.exe"


def main() -> int:
    ap = argparse.ArgumentParser(description="安裝炸彈用的 StepMania 素材到打包來源樹")
    ap.add_argument("--sm", default=DEF_SM, help=f"StepMania 根目錄 (預設 {DEF_SM})")
    ap.add_argument("--repo", default=str(Path(__file__).resolve().parents[2]), help="65_remake repo 根")
    ap.add_argument("--ffmpeg", help="ffmpeg 路徑 (轉 ogg→wav 用)")
    a = ap.parse_args()

    sm, repo = Path(a.sm), Path(a.repo)
    off = repo / "assets" / "sdox_offline"
    if not off.is_dir():
        print(f"找不到打包來源樹：{off}", file=sys.stderr)
        return 1

    rc = 0

    # 1) 爆炸圖（直接複製；黑底無 alpha，遊戲端用 additive 去背）
    src_png = sm / SKIN
    dst_png = off / "Extracted" / "NOTEIMAGE" / "BOMB_EXPLODE.png"
    if src_png.exists():
        dst_png.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src_png, dst_png)
        print(f"[ok] {dst_png.relative_to(repo)}  ({dst_png.stat().st_size} bytes)")
    else:
        print(f"[缺] 找不到爆炸圖：{src_png}", file=sys.stderr)
        rc = 1

    # 2) 爆炸音（ogg → wav，因為 ScreenGameplay 的 PlaySe 只讀 SE/*.wav）
    src_ogg = sm / THEME
    dst_wav = off / "SE" / "player_mine.wav"
    if not src_ogg.exists():
        print(f"[缺] 找不到爆炸音：{src_ogg}", file=sys.stderr)
        return 1
    ff = a.ffmpeg or shutil.which("ffmpeg") or (FFMPEG_FALLBACK if Path(FFMPEG_FALLBACK).exists() else None)
    if not ff:
        print("[缺] 找不到 ffmpeg —— 請用 --ffmpeg 指定，或自行把該 ogg 轉成 SE/player_mine.wav", file=sys.stderr)
        return 1
    dst_wav.parent.mkdir(parents=True, exist_ok=True)
    r = subprocess.run([ff, "-y", "-v", "error", "-i", str(src_ogg), str(dst_wav)])
    if r.returncode != 0 or not dst_wav.exists():
        print("[缺] ffmpeg 轉檔失敗", file=sys.stderr)
        return 1
    print(f"[ok] {dst_wav.relative_to(repo)}  ({dst_wav.stat().st_size} bytes)")

    print("\n完成。接著跑 tools/package_build.ps1，這兩個檔會被鏡射進 build 的 DATA/。")
    print("（編輯器另外吃 data_root.txt 指到的那棵 DATA，需要的話一併複製過去。）")
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
