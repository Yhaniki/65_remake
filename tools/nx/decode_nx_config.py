#!/usr/bin/env python3
"""解 SDO 的 serverconfig（歌單順序 + NEW/HOT/推薦/古典 標籤）。

吃三種輸入，格式一樣，只差外面那層混淆：

  * `ServerConfigND.dat`      伺服器下發的那份 —— 明碼
  * `patch Datas\\config2`     [NX]Patch 版的 ServerConfigND —— XOR 混淆 (seed 0xC3)
  * `patch Datas\\config1`     [NX]Patch 版的 OPEN_BPM.dat    —— 同一套 (seed 0x5B)

用法：
    python decode_nx_config.py "<path>"                 # 印出歌曲表摘要
    python decode_nx_config.py "<path>" -o out.bin      # 順便存明碼
    python decode_nx_config.py "<path>" --ids           # 印出完整順序 (id 一行一個)

格式與逆向依據見 docs/reverse-engineering/SDO_SERVERCONFIG.md。
"""
import argparse
import struct
import sys

MAGIC = b'ServerConfig0073'
SEEDS = (0xC3, 0x5B)          # config2 / config1


def keystream(i, seed):
    """NXPatch ReadFile hook 的逐位元組金鑰：位移 → key（見 .nxd 0x124fa1e）。"""
    v = ((i & 0xFF) ^ ((i >> 8) & 0xFF) ^ seed) & 0xFF
    v = ((v << 3) | (v >> 5)) & 0xFF
    return (v + 0x3D) & 0xFF


def deobfuscate(data, seed):
    return bytes(b ^ keystream(i, seed) for i, b in enumerate(data))


def plain(data):
    """回傳明碼內容（已剝掉最外面的 4-byte size）。認不出來就 raise。"""
    for cand in (data,) + tuple(deobfuscate(data, s) for s in SEEDS):
        if cand[4:4 + len(MAGIC)] == MAGIC:
            return cand
    raise ValueError('不是 serverconfig（找不到 magic ServerConfig0073）')


def parse(buf):
    """buf = 明碼整檔。回傳 (arrays, tables)：8 張 u32 陣列 + 3 張 12-byte 歌曲表。"""
    body = buf[4:]
    if body[:len(MAGIC)] != MAGIC:
        raise ValueError('magic 不符')
    pos = 0x14                                  # 16B magic + 4B 版本欄
    arrays = []
    for _ in range(8):
        n = struct.unpack_from('<I', body, pos)[0]; pos += 4
        arrays.append(list(struct.unpack_from('<%dI' % n, body, pos)) if n else [])
        pos += n * 4
    pos += 40                                   # 固定 5 組 × 4×u16 旗標
    tables = []
    for _ in range(3):
        n = struct.unpack_from('<I', body, pos)[0]; pos += 4
        rows = []
        for k in range(n):
            r = body[pos + k * 12: pos + k * 12 + 12]
            rows.append({
                'id': struct.unpack_from('<I', r, 0)[0],
                'new': r[4], 'hot': r[5], 'recommend': r[6],
                'hidden': r[7], 'classical': r[8],
            })
        pos += n * 12
        tables.append(rows)
    return arrays, tables


def badge(row):
    """官方顯示優先序：NEW > HOT > 推薦 > 古典（一列最多一個）。"""
    if row['hidden']:
        return 'hidden'
    for k in ('new', 'hot', 'recommend', 'classical'):
        if row[k]:
            return k
    return ''


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('path')
    ap.add_argument('-o', '--out', help='把明碼寫到這個檔')
    ap.add_argument('--ids', action='store_true', help='印出表 0 的完整順序')
    ap.add_argument('--table', type=int, default=0, help='要看第幾張歌曲表 (0=SDO, 1=AU, 2=第三模式)')
    args = ap.parse_args(argv)

    raw = open(args.path, 'rb').read()
    buf = plain(raw)
    if args.out:
        open(args.out, 'wb').write(buf)
        print('明碼 → %s (%d bytes)' % (args.out, len(buf)))

    arrays, tables = parse(buf)
    print('u32 陣列筆數:', [len(a) for a in arrays])
    for i, rows in enumerate(tables):
        n = len(rows)
        cnt = {k: sum(1 for r in rows if r[k]) for k in ('new', 'hot', 'recommend', 'classical', 'hidden')}
        print('表 %d: %d 首  %s' % (i, n, cnt))

    rows = tables[args.table]
    tagged = [(r['id'], badge(r)) for r in rows if badge(r)]
    print('\n有標籤的 %d 首（表 %d，順序即選單順序；畫面是反序，最後一筆在最上面）:' % (len(tagged), args.table))
    for sid, b in tagged:
        print('  %5d  %s' % (sid, b))
    if args.ids:
        print('\n完整順序:')
        for r in rows:
            print(r['id'])
    return 0


if __name__ == '__main__':
    sys.exit(main())
