# -*- coding: utf-8 -*-
"""sdopak.py 的單元測試 + 跨語言契約 fixture。

跨語言契約怎麼驗:
  **不是**比對 byte 完全一致 —— C# 的 DeflateStream 與 Python 的 zlib 對同一份輸入會產生
  不同但都合法的 deflate 位元流,永遠對不起來。要驗的是「C# 讀得懂 Python 產的檔」。

  做法:這支測試產生一個涵蓋所有特性的 fixture(store / deflate / 全檔加密 / 表頭加密 /
  whiteout / 中文路徑),寫到 Assets/Tests/EditMode/Fixtures/contract_v1.pak.bytes;
  C# 的 PakTests.ReadsPythonProducedPak 讀它並逐項驗內容。兩邊的實作一漂移,那個測試就紅。

  fixture 是 deterministic 的(這裡有測),所以重新產生不會製造假 diff。
  要更新:python tools/tests/test_sdopak.py --write

跑:python tools/tests/test_sdopak.py
"""

from __future__ import annotations

import hashlib
import struct
import sys
import unittest
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import sdopak  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
FIXTURE = (REPO / "65" / "My project" / "Assets" / "Tests" / "EditMode"
           / "Fixtures" / "contract_v1.pak.bytes")

#: fixture 的內容 —— C# 端的 PakTests 逐項對這張表。改這裡就要同步改那邊。
FIXTURE_PAK_ID = 42
FIXTURE_FILES = {
    "UI/GAMEPLAY/x.png": b"hello from python",
    # 壓得下去 → 走 deflate 分支
    "AVATAR/body.dds": b"compressible " * 64,
    # 壓不下去 → 走 store 分支（deflate 後反而變大時就存原樣）
    "SCENE/tiny.msh": b"\x01\x02\x03",
    # 非 ASCII 路徑：雜湊只轉 ASCII 大寫，多位元組字元不能被動到
    "MUSIC/歌曲/測試.gn": b"cjk path payload",
}
FIXTURE_HEADER_ONLY = "MUSIC/song.mp3"          # 只加密前 4096 bytes
FIXTURE_WHITEOUT = "SCENE/deleted.msh"


def header_only_payload() -> bytes:
    """不重複的序列。

    ⚠️ 別用 ``(i * 31) & 0xFF`` —— 那個對 i 的週期剛好 256，整段會是同一個 256-byte
    圖樣重複，頭尾兩段完全相同 → 「開頭有沒有被加密」根本驗不出來（踩過一次）。
    """
    out = bytearray()
    state = 12345
    for _ in range(sdopak.HEADER_CRYPT_BYTES + 512):
        state = (state * 1103515245 + 12345) & 0xFFFFFFFF
        out.append((state >> 16) & 0xFF)
    return bytes(out)


def build_fixture() -> bytes:
    w = sdopak.PakWriter(FIXTURE_PAK_ID, encrypt=True)
    for path, data in FIXTURE_FILES.items():
        w.add(path, data)
    w.add(FIXTURE_HEADER_ONLY, header_only_payload(),
          compress=False, crypt_range=sdopak.CRYPT_HEADER_ONLY)
    w.add_whiteout(FIXTURE_WHITEOUT)
    return w.build()


class PathTests(unittest.TestCase):
    def test_normalize_basics(self):
        self.assertEqual("UI/GAMEPLAY/x.png", sdopak.normalize(r"UI\GAMEPLAY\x.png"))
        self.assertEqual("A/B", sdopak.normalize("/A//B/"))
        self.assertEqual("A/C", sdopak.normalize("A/B/../C"))
        self.assertEqual("Ui/GamePlay/X.Png", sdopak.normalize("Ui/GamePlay/X.Png"))

    def test_normalize_rejects_escapes(self):
        # pak 內一條 ../../windows/… 就能讓解包寫到任意位置 —— 一定要在這裡擋掉。
        for bad in ("..", "A/../..", r"..\..\windows\system32\drivers\etc\hosts",
                    r"C:\Windows", ""):
            self.assertIsNone(sdopak.normalize(bad), bad)

    def test_path_hash_matches_csharp_vectors(self):
        # 釘死 FNV-1a 64（對 ASCII 大寫後的 UTF-8）—— C# 的 VfsPath.Hash 必須算出同樣的值。
        self.assertEqual(0xCBF29CE484222325, sdopak.path_hash(""))
        self.assertEqual(0x4A294E74868CF16E, sdopak.path_hash("UI/GAMEPLAY/x.png"))
        self.assertEqual(sdopak.path_hash("AVATAR/FEMALE.HRC"),
                         sdopak.path_hash("avatar/female.hrc"))
        self.assertEqual(sdopak.path_hash("歌/曲.gn"), sdopak.path_hash("歌/曲.GN"))
        self.assertNotEqual(sdopak.path_hash("歌/曲.gn"), sdopak.path_hash("歌/曲2.gn"))

    def test_is_reserved(self):
        for p in ("PROFILE/config.ini", "profile/x", "ADDON/SONG/a.osu",
                  "CACHE/x.json", "REPLAY/r.rpy"):
            self.assertTrue(sdopak.is_reserved(p), p)
        for p in ("AVATAR/x.dds", "PROFILES/x", "UI/PROFILE/x.png", ""):
            self.assertFalse(sdopak.is_reserved(p), p)


class CryptoTests(unittest.TestCase):
    def test_keys_are_per_volume_and_per_purpose(self):
        self.assertNotEqual(sdopak.data_key(1), sdopak.data_key(2))
        self.assertNotEqual(sdopak.data_key(1), sdopak.index_key(1))
        self.assertEqual(16, len(sdopak.data_key(1)))
        self.assertEqual(32, len(sdopak.mac_key(1)))

    def test_keystream_is_seekable(self):
        # 從中間任意位置起算，結果要跟整段加密的同一段一致 —— 隨機存取的前提。
        key = sdopak.data_key(1)
        whole = bytearray(64)
        sdopak.xor_keystream(key, whole, 0, len(whole), 0)
        for at in range(1, 40, 7):
            part = bytearray(16)
            sdopak.xor_keystream(key, part, 0, len(part), at)
            self.assertEqual(bytes(whole[at:at + 16]), bytes(part), f"位移 {at} 對不上")

    def test_keystream_is_symmetric(self):
        key = sdopak.data_key(1)
        data = b"round trip me"
        buf = bytearray(data)
        sdopak.xor_keystream(key, buf, 0, len(buf), 100)
        self.assertNotEqual(data, bytes(buf))
        sdopak.xor_keystream(key, buf, 0, len(buf), 100)
        self.assertEqual(data, bytes(buf))

    def test_crc32_matches_the_standard(self):
        # C# 的 PakFormat.Crc32 釘死同樣的向量。
        self.assertEqual(0xCBF43926, zlib.crc32(b"123456789") & 0xFFFFFFFF)
        self.assertEqual(0x414FA339,
                         zlib.crc32(b"The quick brown fox jumps over the lazy dog") & 0xFFFFFFFF)


class WriterTests(unittest.TestCase):
    def test_rejects_reserved_paths(self):
        # 這四個是可寫的明碼區，打包進去只會製造「玩家存檔被 pak 蓋掉」那種災難。
        for p in ("PROFILE/config.ini", "ADDON/SONG/a.osu", "CACHE/x.json", "REPLAY/r.rpy"):
            with self.assertRaises(ValueError, msg=p):
                sdopak.PakWriter(1).add(p, b"x")

    def test_rejects_escaping_paths(self):
        for p in ("../../windows/system32/x", r"C:\Windows\x"):
            with self.assertRaises(ValueError, msg=p):
                sdopak.PakWriter(1).add(p, b"x")

    def test_output_is_deterministic(self):
        # patch diff 的前提：同輸入 → 同 bytes；加入順序不影響輸出。
        def make(order):
            w = sdopak.PakWriter(1)
            for p, d in order:
                w.add(p, d)
            return w.build()

        a = [("b.txt", b"bbb"), ("a.txt", b"aaa")]
        self.assertEqual(make(a), make(a))
        self.assertEqual(make(a), make(list(reversed(a))))

    def test_case_variants_of_one_path_are_not_a_collision(self):
        # 雜湊本來就大小寫不敏感，這兩條算同一個鍵，不該被當成碰撞炸掉。
        sdopak.PakWriter(1).add("A/x.dds", b"1").add("a/X.DDS", b"2").build()

    def test_hash_collision_is_a_hard_failure(self):
        # 靜默帶過的後果是某個資產永遠讀到另一個檔的內容。天然碰撞造不出來（2^64），
        # 所以把雜湊函式換掉來模擬。
        w = sdopak.PakWriter(1).add("A/x.dds", b"1").add("B/y.dds", b"2")
        orig = sdopak.path_hash
        try:
            sdopak.path_hash = lambda p: 1234
            with self.assertRaises(RuntimeError):
                w.build()
        finally:
            sdopak.path_hash = orig

    def test_header_layout_is_as_specified(self):
        blob = sdopak.PakWriter(3).add("a.txt", b"x").build()
        self.assertEqual(sdopak.MAGIC, blob[0:8])
        self.assertEqual(sdopak.FORMAT_VERSION, struct.unpack_from("<I", blob, 0x08)[0])
        self.assertEqual(1, struct.unpack_from("<I", blob, 0x10)[0])           # entryCount
        self.assertEqual(3, struct.unpack_from("<I", blob, 0x14)[0])           # pakId
        self.assertEqual(sdopak.HEADER_SIZE, struct.unpack_from("<Q", blob, 0x28)[0])
        self.assertEqual(40, struct.calcsize("<QIIQIHHII"))
        self.assertEqual(40, sdopak.ENTRY_SIZE)

    def test_encryption_hides_payload_and_paths(self):
        blob = sdopak.PakWriter(7, encrypt=True).add("SCENE/a.msh", b"secret payload").build()
        self.assertNotIn(b"secret payload", blob)
        self.assertNotIn(b"SCENE/a.msh", blob)      # 索引也加密了

    def test_header_only_encryption_leaves_the_tail_clear(self):
        payload = header_only_payload()
        blob = sdopak.PakWriter(3, encrypt=True).add(
            "MUSIC/song.mp3", payload, compress=False,
            crypt_range=sdopak.CRYPT_HEADER_ONLY).build()
        self.assertIn(payload[-256:], blob, "尾段應該維持明文（串流播放只需解前 4KB）")
        self.assertNotIn(payload[:256], blob, "前 4KB 應該被加密")


class FixtureTests(unittest.TestCase):
    def test_fixture_is_deterministic(self):
        self.assertEqual(build_fixture(), build_fixture())

    def test_fixture_on_disk_is_up_to_date(self):
        self.assertTrue(FIXTURE.exists(),
                        f"缺 fixture：跑 python tools/tests/{Path(__file__).name} --write")
        self.assertEqual(
            build_fixture(), FIXTURE.read_bytes(),
            "fixture 過期了 —— 打包器的輸出變了。確認那是刻意的之後跑 "
            f"python tools/tests/{Path(__file__).name} --write，"
            "並確認 C# 的 PakTests.ReadsPythonProducedPak 仍然綠。")


if __name__ == "__main__":
    if "--write" in sys.argv:
        FIXTURE.parent.mkdir(parents=True, exist_ok=True)
        blob = build_fixture()
        FIXTURE.write_bytes(blob)
        print(f"wrote {FIXTURE}")
        print(f"  {len(blob)} bytes, sha256={hashlib.sha256(blob).hexdigest()}")
    else:
        unittest.main()
