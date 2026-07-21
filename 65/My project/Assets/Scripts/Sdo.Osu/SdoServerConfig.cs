using System;
using System.Collections.Generic;
using System.IO;

namespace Sdo.Osu
{
    /// <summary>
    /// 選歌列的標籤。官方一列**最多一個**，判斷順序就是這個列舉的順序
    /// （NEW &gt; HOT &gt; 推薦 &gt; 古典，見 SDO_SERVERCONFIG.md §5）。
    /// </summary>
    public enum SongBadge { None = 0, New = 1, Hot = 2, Recommend = 3, Classical = 4 }

    /// <summary>serverconfig 歌曲表的一列。</summary>
    public sealed class SdoServerConfigSong
    {
        /// <summary>歌曲編號 = <c>fileId % 10000</c>（sdom0040K.gn → fileId 10040 → 40）。</summary>
        public int SongId;
        public SongBadge Badge;
        /// <summary>表中的 +7 欄：非 0 = 官方端「未開放/隱藏」。重製版只記錄不隱藏（玩家自己放進來的歌不該被藏掉）。</summary>
        public bool Hidden;
        /// <summary>在表中的列號（0 = 第一列）。**畫面是反序**：列號越大越上面。</summary>
        public int Order;
    }

    /// <summary>
    /// 讀 SDO 的 <c>serverconfig</c>（<c>serverconfig.dat</c> / <c>ServerConfigND.dat</c>，
    /// [NX]Patch 則是 <c>patch Datas\config2</c>）。
    ///
    /// **選歌畫面的歌曲順序與 NEW/HOT/推薦/古典 標籤都住在這裡，不在 SongList.dat**：官方客戶端拿這張
    /// 12-byte/首的表當外層迴圈去 join SongList.dat 的記錄，所以「表的排列順序 = 選單順序」，而標籤是把表裡的
    /// 旗標寫進記錄的 +0xC4/0xC5/0xC6/0x1C3。完整逆向（版面、加密、位址、實測數字）見
    /// <c>docs/reverse-engineering/SDO_SERVERCONFIG.md</c>。
    ///
    /// 這裡只解「SDO 模式」那張表（表 0）——AU/JAM 兩張是別的模式用的，重製版沒有。
    /// 純邏輯、不碰 Unity、任何壞資料都只會回空清單（絕不丟例外）。
    /// </summary>
    public static class SdoServerConfig
    {
        /// <summary>明碼內容開頭的 16 bytes（位於檔案 +4，前面 4 bytes 是 size 欄）。</summary>
        public static readonly byte[] Magic =
        {
            (byte)'S', (byte)'e', (byte)'r', (byte)'v', (byte)'e', (byte)'r', (byte)'C', (byte)'o',
            (byte)'n', (byte)'f', (byte)'i', (byte)'g', (byte)'0', (byte)'0', (byte)'7', (byte)'3',
        };

        /// <summary>[NX]Patch 的 ReadFile hook 用的兩個 seed（config2 = 0xC3、config1 = 0x5B）。</summary>
        public static readonly int[] NxSeeds = { 0xC3, 0x5B };

        private const int HeaderSize = 4;           // 檔頭的 size 欄
        private const int VersionFieldSize = 4;     // magic 之後那個版本/時戳欄
        private const int FlagBlockSize = 40;       // 固定 5 組 × 4×u16
        private const int DwordArrays = 8;
        private const int RowSize = 12;

        // ---------------- 混淆 ----------------

        /// <summary>
        /// [NX]Patch 的逐位元組金鑰（.nxd 0x124fa1e）：
        /// <c>k = rol8((pos ^ (pos>>8) ^ seed), 3) + 0x3D</c>，pos 就是檔案位移。對稱，加解密同一式。
        /// </summary>
        public static byte KeyAt(int pos, int seed)
        {
            int v = ((pos & 0xFF) ^ ((pos >> 8) & 0xFF) ^ seed) & 0xFF;
            v = ((v << 3) | (v >> 5)) & 0xFF;
            return (byte)((v + 0x3D) & 0xFF);
        }

        /// <summary>整段套上 <see cref="KeyAt"/>（回傳新陣列；null → null）。</summary>
        public static byte[] Deobfuscate(byte[] data, int seed)
        {
            if (data == null) return null;
            var res = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) res[i] = (byte)(data[i] ^ KeyAt(i, seed));
            return res;
        }

        /// <summary>檔案 +4 起是不是 magic。</summary>
        public static bool LooksPlain(byte[] data)
        {
            if (data == null || data.Length < HeaderSize + Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++)
                if (data[HeaderSize + i] != Magic[i]) return false;
            return true;
        }

        /// <summary>
        /// 明碼化：本來就是明碼就原樣回傳，否則試 [NX] 的兩個 seed。都不像 serverconfig → null。
        /// （先只解前 20 bytes 判 seed，中了才整份解，省得對錯檔案做整檔 XOR。）
        /// </summary>
        public static byte[] Plain(byte[] data)
        {
            if (LooksPlain(data)) return data;
            if (data == null || data.Length < HeaderSize + Magic.Length) return null;
            foreach (var seed in NxSeeds)
            {
                bool ok = true;
                for (int i = 0; i < Magic.Length && ok; i++)
                    ok = (byte)(data[HeaderSize + i] ^ KeyAt(HeaderSize + i, seed)) == Magic[i];
                if (ok) return Deobfuscate(data, seed);
            }
            return null;
        }

        // ---------------- 解析 ----------------

        /// <summary>整份檔案 → SDO 模式歌曲表（表 0）。認不得/壞掉 → 空清單。</summary>
        public static List<SdoServerConfigSong> Parse(byte[] data)
        {
            var res = new List<SdoServerConfigSong>();
            var buf = Plain(data);
            if (buf == null) return res;

            // magic 之後：8 張 u32 陣列 → 40 bytes 旗標 → 3 張 12-byte 表（第一張就是要的）
            int pos = HeaderSize + Magic.Length + VersionFieldSize;
            for (int i = 0; i < DwordArrays; i++)
            {
                if (!ReadCount(buf, ref pos, 4, out int n)) return res;
                pos += n * 4;
            }
            pos += FlagBlockSize;
            if (!ReadCount(buf, ref pos, RowSize, out int rows)) return res;   // 表 0 = SDO 模式（另外兩張是 AU/JAM，不解）
            for (int i = 0; i < rows; i++)
            {
                int o = pos + i * RowSize;
                var row = new SdoServerConfigSong
                {
                    SongId = (int)ReadU32(buf, o),
                    Hidden = buf[o + 7] != 0,
                    Order = i,
                };
                // 官方的判斷順序（互斥）：NEW → HOT → 推薦 → 古典
                if (buf[o + 4] != 0) row.Badge = SongBadge.New;
                else if (buf[o + 5] != 0) row.Badge = SongBadge.Hot;
                else if (buf[o + 6] != 0) row.Badge = SongBadge.Recommend;
                else if (buf[o + 8] != 0) row.Badge = SongBadge.Classical;
                res.Add(row);
            }
            return res;
        }

        /// <summary>依 <see cref="SdoServerConfigSong.SongId"/> 建索引（重複 id 取第一筆）。</summary>
        public static Dictionary<int, SdoServerConfigSong> ById(List<SdoServerConfigSong> rows)
        {
            var map = new Dictionary<int, SdoServerConfigSong>();
            if (rows == null) return map;
            foreach (var r in rows)
                if (r != null && !map.ContainsKey(r.SongId)) map[r.SongId] = r;
            return map;
        }

        /// <summary>fileId → 表的 key（官方 join 也是這樣算：<c>fileId % 10000</c>）。</summary>
        public static int SongIdOf(int fileId) => fileId <= 0 ? 0 : fileId % 10000;

        // ---------------- 檔案位置 ----------------

        /// <summary>檔名候選，依「越可能是最新的」排前面。</summary>
        public static readonly string[] FileNames = { "ServerConfigND.dat", "config2", "serverconfig.dat" };

        /// <summary>
        /// 一個 <c>.gn</c> 歌包資料夾（通常是 <c>&lt;pack&gt;\patch music</c>）要去哪找它自己的 serverconfig。
        /// 純字串運算，不碰檔案系統，好測。順序 = 掃描時的嘗試順序。
        /// </summary>
        public static List<string> ConfigCandidates(string songDir)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(songDir)) return res;
            string parent = null;
            try { parent = Path.GetDirectoryName(songDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch { parent = null; }

            foreach (var n in FileNames) Add(res, songDir, n);                       // 包在歌資料夾裡
            if (!string.IsNullOrEmpty(parent))
            {
                foreach (var n in FileNames) Add(res, Combine(parent, "patch Datas"), n);   // [NX]Patch 的擺法
                foreach (var n in FileNames) Add(res, Combine(parent, "Datas"), n);         // 官方 Datas\
                foreach (var n in FileNames) Add(res, parent, n);                           // 客戶端根目錄（music\ 的隔壁）
            }
            return res;
        }

        private static void Add(List<string> res, string dir, string name)
        {
            var p = Combine(dir, name);
            if (p != null) res.Add(p);
        }

        private static string Combine(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return null;
            try { return Path.Combine(a, b); }
            catch { return null; }
        }

        // ---------------- helpers ----------------

        /// <summary>讀 u32 筆數並把 pos 推到資料起點；筆數不合理/超出檔尾 → false。</summary>
        private static bool ReadCount(byte[] buf, ref int pos, int elemSize, out int count)
        {
            count = 0;
            if (pos < 0 || pos + 4 > buf.Length) return false;
            long n = ReadU32(buf, pos);
            pos += 4;
            if (n < 0 || n > int.MaxValue / Math.Max(1, elemSize)) return false;
            long end = (long)pos + n * elemSize;
            if (end > buf.Length) return false;
            count = (int)n;
            return true;
        }

        private static uint ReadU32(byte[] b, int o)
            => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }
}
