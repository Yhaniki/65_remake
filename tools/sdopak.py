"""SDOPAK v1 —— 格式的 Python 實作（打包端）。

C# 那邊的對應實作在 65/My project/Assets/Scripts/Sdo.Settings/Vfs/：
  PakFormat.cs（版面 + CRC32）、PakCrypto.cs（金鑰派生 + AES-CTR + HMAC）、
  PakWriter.cs（寫）、PakProvider.cs（讀）。
**這兩份是同一個契約的兩半，改一邊就要改另一邊，而且要昇版號。**
規格見 docs/architecture/data-packaging.md §3 / §5。

C# 端的 PakTests.cs 有 19 個測試釘死了行為（CRC 向量、determinism、whiteout、
壞檔偵測、金鑰流可 seek…）。這支要產出通得過同樣檢查的檔 —— tests/test_sdopak.py
會用它產檔、再用 C# 讀回來對。

⚠️ 加密是混淆不是保護：金鑰必然在用戶端執行檔裡。目標只有「不能整包拷走直接用」
   跟「不能隨手改 .dds 作弊」，不要期待更多。
"""

from __future__ import annotations

import hashlib
import hmac
import io
import struct
import zlib
from dataclasses import dataclass
from typing import Iterable, Sequence

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

# ---------------------------------------------------------------- 格式常數

MAGIC = b"SDOPAK\x00\x01"
FORMAT_VERSION = 1
HEADER_SIZE = 64
ENTRY_SIZE = 40

COMPRESSION_STORE = 0
COMPRESSION_DEFLATE = 1

CRYPT_NONE = 0
CRYPT_WHOLE = 1
CRYPT_HEADER_ONLY = 2
HEADER_CRYPT_BYTES = 4096

FLAG_INDEX_ENCRYPTED = 1 << 0
FLAG_DATA_ENCRYPTED = 1 << 1

WHITEOUT_RAW_SIZE = 0xFFFFFFFF

#: 永遠不進 pak 的頂層目錄 —— 玩家可寫的明碼區。打包進去只會製造
#: 「玩家存檔被 pak 蓋掉」那種災難。對應 VfsPath.ReservedRoots。
RESERVED_ROOTS = ("PROFILE", "ADDON", "CACHE", "REPLAY")


# ---------------------------------------------------------------- 路徑

def normalize(path: str) -> str | None:
    """任意寫法的相對路徑 → 正規形式（'/' 分隔、無前導斜線、摺疊 . 與 ..）。

    無效 → None：空、含 ':'（絕對路徑）、或 '..' 摺疊後逃出根。
    逃出根一定要擋 —— 否則 pak 內一條 ../../windows/… 就能讓解包寫到任意位置。
    對應 C# 的 VfsPath.Normalize。
    """
    if not path:
        return None
    if ":" in path:
        return None
    stack: list[str] = []
    for part in path.replace("\\", "/").split("/"):
        if part == "" or part == ".":
            continue
        if part == "..":
            if not stack:
                return None
            stack.pop()
            continue
        stack.append(part)
    return "/".join(stack)


def is_reserved(normalized: str) -> bool:
    """正規化路徑的第一段是不是 reserved 目錄（大小寫不敏感）。"""
    if not normalized:
        return False
    head = normalized.split("/", 1)[0]
    return head.upper() in RESERVED_ROOTS


def path_hash(normalized: str) -> int:
    """FNV-1a 64，對 **ASCII 大寫後**的 UTF-8 bytes。對應 C# 的 VfsPath.Hash。

    只轉 ASCII 的 a-z：原始資料樹是純 ASCII 檔名而 NTFS 大小寫不敏感，程式碼裡對
    同一個檔大小寫混用，所以查表必須大小寫不敏感；但整體 upper() 會踩到土耳其語
    i/İ 那類 locale 陷阱。UTF-8 續接位元組都 >= 0x80，只動 ASCII 區間完全無害。
    """
    h = 0xCBF29CE484222325
    for b in (normalized or "").encode("utf-8"):
        if 0x61 <= b <= 0x7A:      # 'a'..'z'
            b -= 32
        h = ((h ^ b) * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return h


# ---------------------------------------------------------------- 金鑰

_SEG0 = bytes((0x53, 0x44, 0x4F, 0x2D, 0x50, 0x41, 0x4B, 0x2D))
_SEG1 = bytes((0x9C, 0x41, 0xE7, 0x0B, 0x76, 0xD2, 0x38, 0xA5))
_SEG2 = bytes((0x1F, 0xB8, 0x64, 0xCA, 0x03, 0x9D, 0x52, 0xE6))
_SEG3 = bytes((0x77, 0x2A, 0xF1, 0x48, 0xBE, 0x05, 0xC3, 0x91))

INFO_DATA = "sdopak:data:"
INFO_INDEX = "sdopak:idx:"
INFO_MAC = "sdopak:mac:"


def master_key() -> bytes:
    return hashlib.sha256(_SEG0 + _SEG1 + _SEG2 + _SEG3).digest()


def hkdf(ikm: bytes, salt: bytes, info: str, length: int) -> bytes:
    """HKDF-SHA256（RFC 5869），length <= 32 → 單一 expand 區塊。對應 C# 的 PakCrypto.Hkdf。"""
    if not 1 <= length <= 32:
        raise ValueError("只支援 1..32")
    prk = hmac.new(salt, ikm, hashlib.sha256).digest()
    okm = hmac.new(prk, info.encode("utf-8") + b"\x01", hashlib.sha256).digest()
    return okm[:length]


def data_key(pak_id: int) -> bytes:
    return hkdf(master_key(), MAGIC, f"{INFO_DATA}{pak_id}", 16)


def index_key(pak_id: int) -> bytes:
    return hkdf(master_key(), MAGIC, f"{INFO_INDEX}{pak_id}", 16)


def mac_key(pak_id: int) -> bytes:
    return hkdf(master_key(), MAGIC, f"{INFO_MAC}{pak_id}", 32)


def xor_keystream(key: bytes, buf: bytearray, offset: int, count: int, stream_pos: int) -> None:
    """AES-128-CTR：把 buf 的一段就地 XOR 金鑰流。加密與解密是同一個操作。

    counter block = 前 8 bytes 為 0、後 8 bytes 是**大端序**的區塊序號（stream_pos // 16）。
    stream_pos 是這段資料在整個金鑰流裡的位移；資料區用「相對資料區起點的絕對位移」，
    索引區從 0 起算。這個參數就是「同金鑰絕不重用 counter」的保證 —— 傳錯等於把加密整個作廢。
    對應 C# 的 PakCrypto.XorKeystream。
    """
    if count <= 0:
        return
    block_index, skip = divmod(stream_pos, 16)

    # 一次生成夠用的金鑰流（含開頭要丟掉的 skip bytes），再逐 byte XOR。
    total = skip + count
    nblocks = (total + 15) // 16
    counters = b"".join(
        b"\x00" * 8 + (block_index + i).to_bytes(8, "big") for i in range(nblocks)
    )
    enc = Cipher(algorithms.AES(key), modes.ECB()).encryptor()
    stream = enc.update(counters) + enc.finalize()

    for i in range(count):
        buf[offset + i] ^= stream[skip + i]


def index_mac(pak_id: int, ciphertext: bytes) -> bytes:
    """HMAC-SHA256 取前 16 bytes。金鑰同樣在執行檔裡 —— 只擋「改了檔沒重簽」。"""
    return hmac.new(mac_key(pak_id), ciphertext, hashlib.sha256).digest()[:16]


# ---------------------------------------------------------------- 寫

def deflate(data: bytes) -> bytes:
    """raw deflate（無 zlib/gzip 表頭）—— 對應 C# 的 DeflateStream。"""
    co = zlib.compressobj(9, zlib.DEFLATED, -15)
    return co.compress(data) + co.flush()


@dataclass
class Item:
    path: str                       # 正規化
    data: bytes | None              # None = whiteout
    compress: bool = True
    crypt_range: int = CRYPT_WHOLE


@dataclass
class _Entry:
    path_hash: int
    raw_size: int
    data_offset: int
    stored_size: int
    compression: int
    crypt_range: int
    crc32: int


class PakWriter:
    """組出一個 .pak。

    輸出是 deterministic 的：條目依 pathHash 排序、沒有時間戳 —— 同輸入產生
    byte-for-byte 相同的檔，那是做 patch diff 的前提。
    """

    def __init__(self, pak_id: int, encrypt: bool = False):
        self.pak_id = pak_id
        self.encrypt = encrypt
        self._items: list[Item] = []

    def add(self, path: str, data: bytes, compress: bool = True,
            crypt_range: int = CRYPT_WHOLE) -> "PakWriter":
        norm = normalize(path)
        if not norm:
            raise ValueError(f"無效的 pak 路徑: {path!r}")
        if is_reserved(norm):
            raise ValueError(f"reserved 目錄不得打包（{'/'.join(RESERVED_ROOTS)}）: {norm}")
        self._items.append(Item(norm, data, compress,
                                crypt_range if self.encrypt else CRYPT_NONE))
        return self

    def add_whiteout(self, path: str) -> "PakWriter":
        """加一筆刪除標記 —— patch 卷用來「拿掉」低層的檔。"""
        norm = normalize(path)
        if not norm:
            raise ValueError(f"無效的 pak 路徑: {path!r}")
        self._items.append(Item(norm, None))
        return self

    def build(self) -> bytes:
        items = sorted(self._items, key=lambda it: (path_hash(it.path), it.path))

        # pathHash 碰撞 → 直接失敗。10 萬條路徑的碰撞機率約 2.7e-10，真的撞到必須是
        # 「改個檔名」而不是靜默帶過 —— 靜默帶過的後果是某個資產永遠讀到另一個檔的內容。
        for a, b in zip(items, items[1:]):
            if path_hash(a.path) == path_hash(b.path) and a.path.upper() != b.path.upper():
                raise RuntimeError(f"pathHash 碰撞: {a.path} vs {b.path} —— 改掉其中一個檔名")

        entries: list[_Entry] = []
        blobs: list[bytes] = []
        cursor = 0

        for it in items:
            if it.data is None:                       # whiteout：不佔資料區
                entries.append(_Entry(path_hash(it.path), WHITEOUT_RAW_SIZE, cursor,
                                      0, COMPRESSION_STORE, CRYPT_NONE, 0))
                blobs.append(b"")
                continue

            crc = zlib.crc32(it.data) & 0xFFFFFFFF
            stored = it.data
            comp = COMPRESSION_STORE
            if it.compress and it.data:
                d = deflate(it.data)
                # 壓不小就存原樣 —— DDS/mp3 這類已壓縮的資料 deflate 後常常反而變大。
                if len(d) < len(it.data):
                    stored, comp = d, COMPRESSION_DEFLATE

            if it.crypt_range != CRYPT_NONE:
                buf = bytearray(stored)
                n = min(HEADER_CRYPT_BYTES, len(buf)) if it.crypt_range == CRYPT_HEADER_ONLY else len(buf)
                xor_keystream(data_key(self.pak_id), buf, 0, n, cursor)
                stored = bytes(buf)

            entries.append(_Entry(path_hash(it.path), len(it.data), cursor,
                                  len(stored), comp, it.crypt_range, crc))
            blobs.append(stored)
            cursor += len(stored)

        index_raw = _write_index(entries, [it.path for it in items])
        index_stored = deflate(index_raw)
        index_compressed = len(index_stored) < len(index_raw)
        if not index_compressed:
            index_stored = index_raw

        flags = 0
        mac = b"\x00" * 16
        if self.encrypt:
            buf = bytearray(index_stored)
            xor_keystream(index_key(self.pak_id), buf, 0, len(buf), 0)
            index_stored = bytes(buf)
            mac = index_mac(self.pak_id, index_stored)
            flags |= FLAG_INDEX_ENCRYPTED
            if any(e.crypt_range != CRYPT_NONE for e in entries):
                flags |= FLAG_DATA_ENCRYPTED

        data_offset = HEADER_SIZE
        index_offset = data_offset + cursor

        header = bytearray(HEADER_SIZE)
        header[0:8] = MAGIC
        struct.pack_into("<I", header, 0x08, FORMAT_VERSION)
        struct.pack_into("<I", header, 0x0C, flags)
        struct.pack_into("<I", header, 0x10, len(entries))
        struct.pack_into("<I", header, 0x14, self.pak_id)
        struct.pack_into("<Q", header, 0x18, index_offset)
        struct.pack_into("<I", header, 0x20, len(index_stored))
        struct.pack_into("<I", header, 0x24, len(index_raw) if index_compressed else len(index_stored))
        struct.pack_into("<Q", header, 0x28, data_offset)
        header[0x30:0x40] = mac

        out = io.BytesIO()
        out.write(bytes(header))
        for b in blobs:
            out.write(b)
        out.write(index_stored)
        return out.getvalue()

    def write_to(self, output_path) -> int:
        data = self.build()
        with open(output_path, "wb") as f:
            f.write(data)
        return len(data)


def _write_index(entries: Sequence[_Entry], paths: Sequence[str]) -> bytes:
    """u32 pathBlobSize + pathBlob（NUL 分隔的 UTF-8）+ Entry[]。對應 C# 的 PakFormat.WriteIndex。"""
    blob = io.BytesIO()
    offsets: list[int] = []
    for p in paths:
        offsets.append(blob.tell())
        blob.write(p.encode("utf-8"))
        blob.write(b"\x00")
    blob_bytes = blob.getvalue()

    out = io.BytesIO()
    out.write(struct.pack("<I", len(blob_bytes)))
    out.write(blob_bytes)
    for e, off in zip(entries, offsets):
        out.write(struct.pack("<QIIQIHHII",
                              e.path_hash, off, e.raw_size, e.data_offset,
                              e.stored_size, e.compression, e.crypt_range, e.crc32, 0))
    return out.getvalue()


# ---------------------------------------------------------------- 串流寫（正式打包用）

@dataclass
class SourceItem:
    """一筆待打包的東西。src_path 與 data 二擇一;兩者皆 None = whiteout。"""
    path: str                       # 正規化
    src_path: str | None = None
    data: bytes | None = None
    compress: bool = True
    crypt_range: int = CRYPT_WHOLE


class PakBuilder:
    """串流版打包器 —— 一次只把**一個檔**讀進記憶體。

    PakWriter（上面那個）會把整包組在記憶體裡,4 GB 的 base_avatar 直接爆掉,所以正式打包走這裡。
    兩者的輸出格式完全相同（同一套 _write_index / 同一組常數）,PakWriter 保留是因為測試寫起來簡單。

    輸出一樣是 deterministic 的:條目依 pathHash 排序、沒有時間戳。
    """

    def __init__(self, pak_id: int, encrypt: bool = False):
        self.pak_id = pak_id
        self.encrypt = encrypt
        self._items: list[SourceItem] = []

    # -- 收集（不做 I/O） --

    def add_file(self, path: str, src_path, compress: bool = True,
                 crypt_range: int = CRYPT_WHOLE) -> "PakBuilder":
        self._items.append(SourceItem(self._check(path), str(src_path), None, compress,
                                      crypt_range if self.encrypt else CRYPT_NONE))
        return self

    def add_bytes(self, path: str, data: bytes, compress: bool = True,
                  crypt_range: int = CRYPT_WHOLE) -> "PakBuilder":
        self._items.append(SourceItem(self._check(path), None, data, compress,
                                      crypt_range if self.encrypt else CRYPT_NONE))
        return self

    def add_whiteout(self, path: str) -> "PakBuilder":
        norm = normalize(path)
        if not norm:
            raise ValueError(f"無效的 pak 路徑: {path!r}")
        self._items.append(SourceItem(norm, None, None))
        return self

    def _check(self, path: str) -> str:
        norm = normalize(path)
        if not norm:
            raise ValueError(f"無效的 pak 路徑: {path!r}")
        if is_reserved(norm):
            raise ValueError(f"reserved 目錄不得打包（{'/'.join(RESERVED_ROOTS)}）: {norm}")
        return norm

    def __len__(self) -> int:
        return len(self._items)

    # -- 寫 --

    def write(self, output_path) -> dict:
        """寫出 .pak,回傳 manifest(下次做 patch diff 與驗證用)。"""
        items = sorted(self._items, key=lambda it: (path_hash(it.path), it.path))

        for a, b in zip(items, items[1:]):
            if path_hash(a.path) == path_hash(b.path) and a.path.upper() != b.path.upper():
                raise RuntimeError(f"pathHash 碰撞: {a.path} vs {b.path} —— 改掉其中一個檔名")

        entries: list[_Entry] = []
        files_manifest: dict[str, dict] = {}
        whiteouts: list[str] = []
        cursor = 0

        with open(output_path, "wb") as out:
            out.write(b"\x00" * HEADER_SIZE)          # 先佔位,最後回頭補

            for it in items:
                if it.src_path is None and it.data is None:      # whiteout
                    entries.append(_Entry(path_hash(it.path), WHITEOUT_RAW_SIZE, cursor,
                                          0, COMPRESSION_STORE, CRYPT_NONE, 0))
                    whiteouts.append(it.path)
                    continue

                raw = it.data if it.data is not None else _read_file(it.src_path)
                crc = zlib.crc32(raw) & 0xFFFFFFFF

                stored, comp = raw, COMPRESSION_STORE
                if it.compress and raw:
                    d = deflate(raw)
                    if len(d) < len(raw):
                        stored, comp = d, COMPRESSION_DEFLATE

                if it.crypt_range != CRYPT_NONE:
                    buf = bytearray(stored)
                    n = min(HEADER_CRYPT_BYTES, len(buf)) if it.crypt_range == CRYPT_HEADER_ONLY else len(buf)
                    xor_keystream(data_key(self.pak_id), buf, 0, n, cursor)
                    stored = bytes(buf)

                out.write(stored)
                entries.append(_Entry(path_hash(it.path), len(raw), cursor,
                                      len(stored), comp, it.crypt_range, crc))
                files_manifest[it.path] = {"size": len(raw), "crc": crc}
                cursor += len(stored)

            index_raw = _write_index(entries, [it.path for it in items])
            index_stored = deflate(index_raw)
            index_compressed = len(index_stored) < len(index_raw)
            if not index_compressed:
                index_stored = index_raw

            flags = 0
            mac = b"\x00" * 16
            if self.encrypt:
                buf = bytearray(index_stored)
                xor_keystream(index_key(self.pak_id), buf, 0, len(buf), 0)
                index_stored = bytes(buf)
                mac = index_mac(self.pak_id, index_stored)
                flags |= FLAG_INDEX_ENCRYPTED
                if any(e.crypt_range != CRYPT_NONE for e in entries):
                    flags |= FLAG_DATA_ENCRYPTED

            index_offset = HEADER_SIZE + cursor
            out.write(index_stored)

            header = bytearray(HEADER_SIZE)
            header[0:8] = MAGIC
            struct.pack_into("<I", header, 0x08, FORMAT_VERSION)
            struct.pack_into("<I", header, 0x0C, flags)
            struct.pack_into("<I", header, 0x10, len(entries))
            struct.pack_into("<I", header, 0x14, self.pak_id)
            struct.pack_into("<Q", header, 0x18, index_offset)
            struct.pack_into("<I", header, 0x20, len(index_stored))
            struct.pack_into("<I", header, 0x24, len(index_raw) if index_compressed else len(index_stored))
            struct.pack_into("<Q", header, 0x28, HEADER_SIZE)
            header[0x30:0x40] = mac

            out.seek(0)
            out.write(bytes(header))

        return {
            "version": 1,
            "pak": str(output_path).replace("\\", "/").rsplit("/", 1)[-1],
            "pakId": self.pak_id,
            "encrypted": bool(self.encrypt),
            "entryCount": len(entries),
            "dataBytes": cursor,
            "files": files_manifest,
            "whiteouts": whiteouts,
        }


def _read_file(path) -> bytes:
    with open(path, "rb") as f:
        return f.read()
