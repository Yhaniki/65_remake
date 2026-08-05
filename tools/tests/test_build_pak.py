# -*- coding: utf-8 -*-
"""build_pak.py 的分卷／patch 規則測試。

跑:python tools/tests/test_build_pak.py
"""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import build_pak  # noqa: E402


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


if __name__ == "__main__":
    unittest.main()
