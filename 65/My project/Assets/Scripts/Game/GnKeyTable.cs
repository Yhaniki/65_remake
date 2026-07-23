using System.Collections.Generic;

namespace Sdo.Game
{
    /// <summary>
    /// 每個 .gn 的解密資訊視圖（enc / seed / innerOff），資料來自 <see cref="SongTable"/>
    /// （StreamingAssets/song_table.csv 的 enc/seed/seed1/seed2/innerOff 欄）。
    ///
    /// 為什麼要預先算好：SDOM 的 .gn 把 LCG seed 藏在檔案之外，原版引擎是暴力搜出來的
    /// （純 Python 一個檔約 5 秒）。表裡存好每個檔的 seed，runtime 就能立刻解。
    /// <see cref="Sdo.Osu.GnChart"/> 是 engine-free（Sdo.Osu 設了 noEngineReferences）讀不到
    /// StreamingAssets，所以由這裡把 seed 餵給 GnChart.Load。又因為 LCG keystream 只取決於 state
    /// 的低 24 bits，整個曲庫只有約 148 個相異 seed —— 全試一輪 + 驗證只要微秒，
    /// 所以就算檔案被改名／搬走／不在表裡也解得開。
    /// </summary>
    public static class GnKeyTable
    {
        // seed/seed1/seed2 是 uint32（會超過 int.MaxValue）→ 存 long，用的時候 cast 成 uint。
        public class Entry
        {
            public string gn; public string enc; public string mode;
            public long seed; public int innerOff; public long seed1; public long seed2;
            public int fileId; public float bpm;
        }

        private static Dictionary<string, Entry> _byGn;     // key = lowercase .gn filename
        private static uint[] _sdomSeeds = new uint[0];

        /// <summary>Look up an entry by .gn path or filename (case-insensitive). Null if absent.</summary>
        public static Entry Get(string gnPathOrName)
        {
            if (string.IsNullOrEmpty(gnPathOrName)) return null;
            EnsureLoaded();
            return _byGn.TryGetValue(System.IO.Path.GetFileName(gnPathOrName).ToLowerInvariant(), out var e) ? e : null;
        }

        /// <summary>All distinct LCG seeds (SDOM + rewu, ~180). GnChart can decrypt any SDOM/rewu .gn by trying these.</summary>
        public static uint[] SdomSeeds { get { EnsureLoaded(); return _sdomSeeds; } }

        /// <summary>
        /// Candidate seeds for a given .gn, ready to pass to <see cref="Sdo.Osu.GnChart.Load"/>:
        /// the file's own seed first (fast path) then every other distinct seed (fallback).
        /// Returns the full distinct set if the file is unknown / not seed-encrypted (ddrm/plain).
        /// </summary>
        public static uint[] SeedsFor(string gnPathOrName)
        {
            EnsureLoaded();
            var e = Get(gnPathOrName);
            if (e == null || (e.enc != "sdom" && e.enc != "rewu")) return _sdomSeeds;
            uint own = (uint)e.seed;
            var list = new List<uint>(_sdomSeeds.Length + 1) { own };
            foreach (var s in _sdomSeeds) if (s != own) list.Add(s);
            return list.ToArray();
        }

        /// <summary>丟掉快取（改過 song_table.csv 之後用）。</summary>
        public static void Invalidate() { _byGn = null; _sdomSeeds = new uint[0]; }

        private static void EnsureLoaded()
        {
            if (_byGn != null) return;
            _byGn = new Dictionary<string, Entry>(System.StringComparer.Ordinal);
            var seeds = new List<uint>(); var seen = new HashSet<uint>();

            foreach (var r in SongTable.Rows)
            {
                if (string.IsNullOrEmpty(r?.gn)) continue;
                _byGn[r.gn] = new Entry
                {
                    gn = r.gn, enc = r.enc, mode = r.mode,
                    seed = r.seed, innerOff = r.innerOff, seed1 = r.seed1, seed2 = r.seed2,
                    fileId = r.fileId, bpm = r.chartBpm > 0f ? r.chartBpm : r.bpm,
                };
                if (r.enc == "sdom" || r.enc == "rewu")   // 兩種都是 LCG seed 加密；seed 池共用
                {
                    uint s = (uint)r.seed;
                    if (seen.Add(s)) seeds.Add(s);
                }
            }
            _sdomSeeds = seeds.ToArray();
        }
    }
}
