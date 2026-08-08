# -*- coding: utf-8 -*-
"""build_pak.py 的分卷／patch 規則測試。

跑:python tools/tests/test_build_pak.py
"""

from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import build_pak  # noqa: E402
import sdopak  # noqa: E402


def write(root: Path, rel: str, data: bytes) -> Path:
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(data)
    return p


def manifest_entry(data: bytes) -> dict:
    return {"size": len(data), "crc": zlib.crc32(data) & 0xFFFFFFFF}


class PatchTests(unittest.TestCase):
    """patch 卷收哪些檔。"""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        root = Path(self._tmp.name)
        self.source = root / "DATA"
        self.out = root / "out"
        self.manifests = root / "manifests"
        self.out.mkdir(parents=True)
        self.manifests.mkdir(parents=True)

        self.same = b"unchanged"
        self.changed = b"changed!!"
        write(self.source, "UI/same.png", self.same)
        write(self.source, "UI/changed.png", self.changed)
        write(self.source, "BGM/bgm_000.ogg", b"loose audio")

        # 既有的一卷:兩個 UI 檔,其中一個之後才變。BGM 從來不在 manifest 裡(它維持散裝)。
        (self.manifests / "base_core.manifest.json").write_text(json.dumps({
            "pak": "base_core.pak", "pakId": 10, "files": {
                "UI/same.png": manifest_entry(self.same),
                "UI/changed.png": manifest_entry(b"the older bytes"),
            },
        }), encoding="utf-8")

    def tearDown(self):
        self._tmp.cleanup()

    def patch(self) -> dict:
        build_pak.build_patch(self.source, self.out, self.manifests, encrypt=False, patch_id=1)
        return json.loads((self.manifests / "patch_001.manifest.json").read_text(encoding="utf-8"))

    def test_takes_only_the_changed_file(self):
        files = self.patch()["files"]
        self.assertIn("UI/changed.png", files)
        self.assertNotIn("UI/same.png", files)

    def test_skips_loose_volumes(self):
        # BGM 是 loose=True:不在任何 manifest 裡 → 不擋的話每個檔都會被判成「新增」,
        # 每產一次 patch 就白白多背一份 BGM(遊戲讀的還是散裝那份)。
        self.assertNotIn("BGM/bgm_000.ogg", self.patch()["files"])

    def test_loose_files_are_not_whiteouts(self):
        # 反過來也不行:散裝的檔不進 pak,但也不能被當成「消失了」而寫成 whiteout。
        self.assertEqual([], self.patch().get("whiteouts", []))

    def test_deleted_file_becomes_a_whiteout(self):
        (self.source / "UI/same.png").unlink()
        m = self.patch()
        self.assertIn("UI/same.png", m["whiteouts"])
        self.assertNotIn("UI/same.png", m["files"])


class ProgressTests(unittest.TestCase):
    """進度列。大卷要跑好幾分鐘,這條回報鍊斷掉就完全看不到動靜。"""

    def build(self, root: Path):
        b = sdopak.PakBuilder(9, encrypt=False)
        b.add_bytes("UI/a.txt", b"a" * 100)
        b.add_bytes("UI/b.txt", b"b" * 50)
        b.add_whiteout("UI/gone.txt")           # whiteout 沒內容,但也要算進「做完幾個」
        seen = []
        b.write(root / "t.pak", progress=lambda n, by: seen.append((n, by)))
        return seen

    def test_reports_every_item_once_and_ends_at_the_total(self):
        with tempfile.TemporaryDirectory() as tmp:
            seen = self.build(Path(tmp))
        self.assertEqual([1, 2, 3], [n for n, _ in seen], "每個項目剛好回報一次、單調遞增")
        self.assertEqual(150, seen[-1][1], "最後一次要等於全部原始 bytes(whiteout 不計入)")

    def test_bar_throttles_but_always_prints_the_last_one(self):
        bar = build_pak.Progress("vol", 3, 150)
        bar.tty = False
        bar.interval = 3600                      # 大到不可能因為時間而印
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            bar(1, 50)                           # 第一筆一定要印:立刻讓人看到它開始跑了
            bar(2, 100)                          # 被節流吃掉
            bar(3, 150)                          # 最後一筆一定要印,否則永遠停在半路
        lines = [ln for ln in out.getvalue().splitlines() if ln.strip()]
        self.assertEqual(2, len(lines), out.getvalue())
        self.assertIn("1/3", lines[0])
        self.assertIn("3/3", lines[-1])

    def test_non_tty_never_emits_carriage_returns(self):
        # 導向 build.log 時噴幾萬個 \r 會讓 log 完全沒法看。
        bar = build_pak.Progress("vol", 1, 10)
        bar.tty = False
        bar.interval = 0
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            bar(1, 10)
        self.assertNotIn("\r", out.getvalue())


if __name__ == "__main__":
    unittest.main()
